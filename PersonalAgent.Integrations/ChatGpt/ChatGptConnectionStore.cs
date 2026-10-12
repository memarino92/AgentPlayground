using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Npgsql;
using PersonalAgent.Contracts.Configuration;

namespace PersonalAgent.Integrations.ChatGpt;

public sealed class ChatGptConnectionStore(IntegrationDatabase Database, ChatGptOAuth OAuth)
{
    private sealed record Pending(string ActorId, Guid Id, long Version, ChatGptAuthorization Authorization);
    private static string Hash(string Value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Value)));
    private string Encrypt<T>(T Value, string Key) => PostgresConfigurationCrypto.Encrypt(JsonSerializer.Serialize(Value), "ChatGPT", Key, Database.EncryptionKey);
    private T Decrypt<T>(string Value, string Key) => JsonSerializer.Deserialize<T>(PostgresConfigurationCrypto.Decrypt(Value, "ChatGPT", Key, Database.EncryptionKey))!;

    public async Task InitializeAsync(CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using var timestamp = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'app' AND table_name = 'configuration_settings' AND column_name = 'updated_at')
            """, connection, transaction);
        var hasTimestamp = (bool)(await timestamp.ExecuteScalarAsync(Token))!;
        await using var command = new NpgsqlCommand($"""
            SELECT pg_advisory_xact_lock(947120042);
            CREATE TABLE IF NOT EXISTS app.chatgpt_hosts(id integer PRIMARY KEY CHECK(id = 1), host_id text NOT NULL);
            CREATE TABLE IF NOT EXISTS app.chatgpt_connections(
                id uuid PRIMARY KEY, actor_id text NOT NULL, client_id text NOT NULL, subject text NOT NULL,
                email text, credentials text NOT NULL, version bigint NOT NULL DEFAULT 1, plan_notified boolean NOT NULL DEFAULT false,
                UNIQUE(actor_id, client_id));
            CREATE TABLE IF NOT EXISTS app.chatgpt_pairings(
                code_hash text PRIMARY KEY, actor_id text NOT NULL, pending text NOT NULL, expires_at timestamptz NOT NULL,
                prepared boolean NOT NULL DEFAULT false);
            INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active{(hasTimestamp ? ", updated_at" : "")})
                SELECT 'Api', 'ChatGpt:ReviewedModels', '["gpt-6.1-sol","gpt-6-astra"]', false, true{(hasTimestamp ? ", now()" : "")}
                WHERE NOT EXISTS(SELECT 1 FROM app.chatgpt_hosts WHERE id = 1)
                ON CONFLICT(scope, key) DO NOTHING;
            INSERT INTO app.chatgpt_hosts(id, host_id) VALUES(1, @host) ON CONFLICT DO NOTHING;
            DELETE FROM app.chatgpt_pairings WHERE expires_at < now();
            """, connection, transaction);
        command.Parameters.AddWithValue("host", "urn:uuid:" + Guid.NewGuid());
        await command.ExecuteNonQueryAsync(Token);
        await transaction.CommitAsync(Token);
    }

    public async Task<IReadOnlyList<ChatGptConnection>> ListAsync(string ActorId, CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT id, email, client_id, credentials, version, plan_notified FROM app.chatgpt_connections WHERE actor_id = @actor ORDER BY id", connection);
        command.Parameters.AddWithValue("actor", ActorId);
        await using var reader = await command.ExecuteReaderAsync(Token);
        List<ChatGptConnection> result = [];
        while (await reader.ReadAsync(Token))
        {
            var id = reader.GetGuid(0);
            var credential = Decrypt<ChatGptCredentials>(reader.GetString(3), id.ToString());
            result.Add(new(id, $"{(reader.IsDBNull(1) ? "ChatGPT account" : reader.GetString(1))} · {id.ToString()[..8]}",
                ChatGptOAuth.PlanEnabled(credential.Scope), !string.IsNullOrEmpty(credential.AccessToken), reader.GetInt64(4),
                !reader.GetBoolean(5) && ChatGptOAuth.PlanEnabled(credential.Scope) && credential.AccessToken.Length > 0));
        }
        return result;
    }

    public async Task<ChatGptPairing> StartAsync(string ActorId, Guid? Id, CancellationToken Token)
    {
        ChatGptCredentials? previous = null;
        long version = 0;
        await using var connection = await Database.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@actor, 947120043))", connection, transaction);
        gate.Parameters.AddWithValue("actor", ActorId);
        await gate.ExecuteNonQueryAsync(Token);
        if (Id is not null)
        {
            await using var read = new NpgsqlCommand("SELECT credentials, version FROM app.chatgpt_connections WHERE id = @id AND actor_id = @actor", connection, transaction);
            read.Parameters.AddWithValue("id", Id.Value);
            read.Parameters.AddWithValue("actor", ActorId);
            await using var reader = await read.ExecuteReaderAsync(Token);
            if (!await reader.ReadAsync(Token)) throw new ChatGptException("Connection not found.");
            previous = Decrypt<ChatGptCredentials>(reader.GetString(0), Id.Value.ToString());
            version = reader.GetInt64(1);
        }
        await using var host = new NpgsqlCommand("SELECT host_id FROM app.chatgpt_hosts WHERE id = 1", connection, transaction);
        var hostId = (string)(await host.ExecuteScalarAsync(Token))!;
        var code = ChatGptOAuth.RandomValue();
        var hash = Hash(code);
        var expires = DateTimeOffset.UtcNow.AddMinutes(10);
        var pending = new Pending(ActorId, Id ?? Guid.NewGuid(), version,
            new(hostId, ChatGptOAuth.RandomValue(), ChatGptOAuth.RandomValue(), ChatGptOAuth.RandomValue(),
                previous?.ClientId, string.IsNullOrEmpty(previous?.IdToken) ? null : previous.IdToken, previous?.Email));
        await using var write = new NpgsqlCommand("""
            DELETE FROM app.chatgpt_pairings WHERE expires_at < now() OR actor_id = @actor;
            INSERT INTO app.chatgpt_pairings(code_hash, actor_id, pending, expires_at) VALUES(@hash, @actor, @pending, @expires);
            """, connection, transaction);
        write.Parameters.AddWithValue("actor", ActorId);
        write.Parameters.AddWithValue("hash", hash);
        write.Parameters.AddWithValue("pending", Encrypt(pending, hash));
        write.Parameters.AddWithValue("expires", expires);
        await write.ExecuteNonQueryAsync(Token);
        await transaction.CommitAsync(Token);
        return new(code, expires);
    }

    public async Task<IReadOnlySet<string>> ReadReviewedModelsAsync(CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT value FROM app.configuration_settings WHERE scope = 'Api' AND key = 'ChatGpt:ReviewedModels' AND is_active AND NOT is_secret", connection);
        var value = await command.ExecuteScalarAsync(Token) as string;
        if (value is null) return new HashSet<string>();
        try { return ChatGptModelPolicy.Parse(value); }
        catch (FormatException) { throw new ChatGptException("The reviewed ChatGPT model policy is invalid. Check Database settings."); }
    }

    public async Task DismissWelcomeAsync(string ActorId, Guid Id, CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var command = new NpgsqlCommand("UPDATE app.chatgpt_connections SET plan_notified = true WHERE id = @id AND actor_id = @actor", connection);
        command.Parameters.AddWithValue("id", Id);
        command.Parameters.AddWithValue("actor", ActorId);
        if (await command.ExecuteNonQueryAsync(Token) != 1) throw new ChatGptException("Connection not found.");
    }

    // Possession of a short-lived pairing code can only complete this user's pending connection.
    // Preparation is one-time, so a second helper cannot reuse the same verifier.
    public async Task<ChatGptAuthorization> PrepareAsync(string Code, CancellationToken Token)
    {
        var hash = Hash(Code);
        await using var connection = await Database.OpenAsync(Token);
        await using var command = new NpgsqlCommand("""
            UPDATE app.chatgpt_pairings SET prepared = true
            WHERE code_hash = @hash AND expires_at > now() AND NOT prepared RETURNING pending
            """, connection);
        command.Parameters.AddWithValue("hash", hash);
        var value = await command.ExecuteScalarAsync(Token) as string ?? throw new ChatGptException("Pairing code expired or was already used. Start again in Settings.");
        return Decrypt<Pending>(value, hash).Authorization;
    }

    public async Task CompleteAsync(string Code, ChatGptCredentials Credentials, CancellationToken Token)
    {
        if (Code.Length > 128 || string.IsNullOrEmpty(Credentials.AccessToken) || Credentials.AccessToken.Length > 32768
            || string.IsNullOrEmpty(Credentials.IdToken) || Credentials.IdToken.Length > 32768
            || string.IsNullOrEmpty(Credentials.Scope) || Credentials.Scope.Length > 4096 || string.IsNullOrEmpty(Credentials.Subject)
            || Credentials.RefreshToken?.Length > 32768 || Credentials.ExpiresAt <= DateTimeOffset.UtcNow
            || Credentials.ExpiresAt > DateTimeOffset.UtcNow.AddDays(1) || string.IsNullOrEmpty(Credentials.ClientId)
            || !Credentials.ClientId.StartsWith("oaiapp_", StringComparison.Ordinal))
            throw new ChatGptException("Invalid ChatGPT credentials.");
        var hash = Hash(Code);
        await using var connection = await Database.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        await using var take = new NpgsqlCommand("DELETE FROM app.chatgpt_pairings WHERE code_hash = @hash AND expires_at > now() AND prepared RETURNING pending", connection, transaction);
        take.Parameters.AddWithValue("hash", hash);
        var value = await take.ExecuteScalarAsync(Token) as string ?? throw new ChatGptException("Pairing code expired or was already completed.");
        var pending = Decrypt<Pending>(value, hash);
        if (pending.Authorization.ClientId is not null && pending.Authorization.ClientId != Credentials.ClientId)
            throw new ChatGptException("The registration changed during sign-in.");
        var identity = await OAuth.ValidateIdentityAsync(Credentials.IdToken, Credentials.ClientId, pending.Authorization.Nonce, Token);
        if (identity.Subject != Credentials.Subject) throw new ChatGptException("The account identity did not match.");
        // Never trust display metadata supplied by the helper.
        Credentials = Credentials with { Email = identity.Email };
        await using var save = new NpgsqlCommand("""
            INSERT INTO app.chatgpt_connections(id, actor_id, client_id, subject, email, credentials)
            SELECT @id, @actor, @client, @subject, @email, @credentials WHERE @version = 0
            ON CONFLICT(id) DO NOTHING
            """, connection, transaction);
        SetParameters(save, pending, Credentials);
        var inserted = await save.ExecuteNonQueryAsync(Token);
        if (inserted == 0)
        {
            await using var update = new NpgsqlCommand("""
                UPDATE app.chatgpt_connections SET email = @email, credentials = @credentials, version = version + 1
                WHERE id = @id AND actor_id = @actor AND client_id = @client AND subject = @subject AND version = @version
                """, connection, transaction);
            SetParameters(update, pending, Credentials);
            if (await update.ExecuteNonQueryAsync(Token) != 1) throw new ChatGptException("Connection changed in another session. Start again in Settings.");
        }
        await transaction.CommitAsync(Token);
    }

    private void SetParameters(NpgsqlCommand Command, Pending Pending, ChatGptCredentials Credentials)
    {
        Command.Parameters.AddWithValue("id", Pending.Id);
        Command.Parameters.AddWithValue("actor", Pending.ActorId);
        Command.Parameters.AddWithValue("client", Credentials.ClientId);
        Command.Parameters.AddWithValue("subject", Credentials.Subject);
        Command.Parameters.AddWithValue("email", (object?)Credentials.Email ?? DBNull.Value);
        Command.Parameters.AddWithValue("credentials", Encrypt(Credentials, Pending.Id.ToString()));
        Command.Parameters.AddWithValue("version", Pending.Version);
    }

    // The row lock serializes rotating refresh tokens across API instances and with disconnect.
    public async Task<ChatGptCredentials> GetCredentialsAsync(string ActorId, Guid Id, CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        var credential = await ReadLockedAsync(connection, transaction, ActorId, Id, Token);
        if (credential.AccessToken.Length == 0) throw new ChatGptException("Reconnect this ChatGPT account in Settings.");
        if (credential.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(2))
        {
            credential = await OAuth.RefreshAsync(credential, Token);
            await SaveLockedAsync(connection, transaction, ActorId, Id, credential, Token);
        }
        await transaction.CommitAsync(Token);
        return credential;
    }

    public async Task<ChatGptDisconnect> DisconnectAsync(string ActorId, Guid Id, CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);
        var credential = await ReadLockedAsync(connection, transaction, ActorId, Id, Token);
        var confirmed = false;
        try { confirmed = await OAuth.RevokeAsync(credential, Token); }
        catch (Exception Exception) when (Exception is HttpRequestException or TaskCanceledException or ChatGptException) { }
        await SaveLockedAsync(connection, transaction, ActorId, Id,
            credential with { AccessToken = "", RefreshToken = null, IdToken = "" }, Token);
        await transaction.CommitAsync(Token);
        return new(confirmed);
    }

    private async Task<ChatGptCredentials> ReadLockedAsync(NpgsqlConnection Connection, NpgsqlTransaction Transaction, string ActorId, Guid Id, CancellationToken Token)
    {
        await using var command = new NpgsqlCommand("SELECT credentials FROM app.chatgpt_connections WHERE id = @id AND actor_id = @actor FOR UPDATE", Connection, Transaction);
        command.Parameters.AddWithValue("id", Id);
        command.Parameters.AddWithValue("actor", ActorId);
        var value = await command.ExecuteScalarAsync(Token) as string ?? throw new ChatGptException("This ChatGPT connection is not available to the current user.");
        return Decrypt<ChatGptCredentials>(value, Id.ToString());
    }

    private async Task SaveLockedAsync(NpgsqlConnection Connection, NpgsqlTransaction Transaction, string ActorId, Guid Id, ChatGptCredentials Credentials, CancellationToken Token)
    {
        await using var command = new NpgsqlCommand("UPDATE app.chatgpt_connections SET credentials = @credentials, version = version + 1 WHERE id = @id AND actor_id = @actor", Connection, Transaction);
        command.Parameters.AddWithValue("id", Id);
        command.Parameters.AddWithValue("actor", ActorId);
        command.Parameters.AddWithValue("credentials", Encrypt(Credentials, Id.ToString()));
        await command.ExecuteNonQueryAsync(Token);
    }
}
