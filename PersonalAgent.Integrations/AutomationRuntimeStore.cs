using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Configuration;

namespace PersonalAgent.Integrations;

// Secrets deliberately live in a class with no generated ToString implementation.
public sealed class AutomationRuntimeSnapshot(long Revision, AutomationRuntimeSettings Settings, string Token)
{
    public long Revision { get; } = Revision;
    public AutomationRuntimeSettings Settings { get; } = Settings;
    public string Token { get; } = Token;
    public AutomationRuntimeView View => new(Revision, Settings, Token.Length > 0);
}

public sealed class AutomationRuntimeStore(IntegrationDatabase Database)
{
    public async Task InitializeAsync(CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            BEGIN;
            SELECT pg_advisory_xact_lock(947120012);
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.automation_runtime (
                id integer PRIMARY KEY CHECK (id = 1), revision bigint NOT NULL, settings text NOT NULL, credential text NOT NULL);
            CREATE TABLE IF NOT EXISTS app.automation_sandboxes (
                run_id uuid NOT NULL, step integer NOT NULL, sandbox_id text, expires_at timestamptz NOT NULL,
                credential text NOT NULL, settings text NOT NULL, token_hash text NOT NULL,
                state text NOT NULL DEFAULT 'Creating', result text, PRIMARY KEY(run_id, step));
            CREATE TABLE IF NOT EXISTS app.automation_operations (
                run_id uuid NOT NULL, step integer NOT NULL, operation_id text NOT NULL, request_hash text NOT NULL,
                kind text NOT NULL, target text NOT NULL, outcome text NOT NULL, policy text NOT NULL,
                result text, created_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(run_id, step, operation_id));
            COMMIT;
            """, Connection);
        await Command.ExecuteNonQueryAsync(Token);
    }

    public async Task<AutomationRuntimeSnapshot> ReadAsync(CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("SELECT revision, settings, credential FROM app.automation_runtime WHERE id = 1", Connection);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        return await Reader.ReadAsync(Token) ? new(Reader.GetInt64(0), JsonSerializer.Deserialize<AutomationRuntimeSettings>(Reader.GetString(1))!, Unprotect(Reader.GetString(2))) : new(0, new(), "");
    }

    public async Task<AutomationRuntimeView> SaveAsync(SaveAutomationRuntime Request, CancellationToken Token)
    {
        if (Request.Settings is null) throw new ArgumentException("Settings are required.");
        await using var Connection = await Database.OpenAsync(Token);
        await using var Transaction = await Connection.BeginTransactionAsync(Token);
        await using var Lock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(947120013)", Connection, Transaction);
        await Lock.ExecuteNonQueryAsync(Token);
        var Current = await ReadAsync(Token);
        if (Current.Revision != Request.ExpectedRevision) throw new IntegrationConflictException();
        var Credential = Request.TokenAction switch
        {
            "keep" when Request.Token is null or "" => Current.Token,
            "clear" when Request.Token is null or "" => "",
            "replace" when Request.Token is { Length: > 0 and <= 4096 } && Request.Token.All(C => C is >= '!' and <= '~') => Request.Token,
            _ => throw new ArgumentException("Choose keep, replace or clear for the Railway token.")
        };
        Validate(Request.Settings, Credential);
        var Revision = Current.Revision + 1;
        await using var Save = new NpgsqlCommand("""
            INSERT INTO app.automation_runtime(id, revision, settings, credential) VALUES (1, @revision, @settings, @credential)
            ON CONFLICT (id) DO UPDATE SET revision = EXCLUDED.revision, settings = EXCLUDED.settings, credential = EXCLUDED.credential
            """, Connection, Transaction);
        Save.Parameters.AddWithValue("revision", Revision);
        Save.Parameters.AddWithValue("settings", JsonSerializer.Serialize(Request.Settings));
        Save.Parameters.AddWithValue("credential", Protect(Credential));
        await Save.ExecuteNonQueryAsync(Token);
        await Transaction.CommitAsync(Token);
        return new(Revision, Request.Settings, Credential.Length > 0);
    }

    public static void Validate(AutomationRuntimeSettings Settings, string Credential)
    {
        if (Settings.EnvironmentId is null || Settings.EnvironmentId.Length > 64 || Settings.Checkpoint is null || Settings.Checkpoint.Length > 128
            || Settings.ImageId is null || Settings.ImageId.Length > 100 || Settings.GatewayUrl is null || Settings.GatewayUrl.Length > 2048)
            throw new ArgumentException("Invalid runner setting length.");
        if (Settings.ReviewMode is not ("Off" or "Shadow" or "Enforce")) throw new ArgumentException("Unknown review mode.");
        if (Settings.ApprovedPackages is null || Settings.ApprovedPackages.Length > 50 || Settings.ApprovedPackages.Any(P => P is null || !Regex.IsMatch(P, @"\A[A-Za-z0-9_.-]{1,100}@[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?\z")))
            throw new ArgumentException("Packages must be exact ID@version entries (at most 50).");
        if (Settings.Enabled && (Credential.Length == 0 || !Guid.TryParse(Settings.EnvironmentId, out _)
            || !Regex.IsMatch(Settings.Checkpoint, @"\A[A-Za-z0-9_.-]{1,128}\z")
            || !Regex.IsMatch(Settings.ImageId, @"\Asha256:[a-f0-9]{64}\z")
            || !Uri.TryCreate(Settings.GatewayUrl, UriKind.Absolute, out var Gateway) || Gateway.Scheme != "https"
            || Gateway.UserInfo.Length > 0 || Gateway.Query.Length > 0 || Gateway.Fragment.Length > 0 || Gateway.AbsolutePath != "/"))
            throw new ArgumentException("Enabled Railway execution requires a token, environment UUID, prepared checkpoint, immutable image ID and HTTPS API origin.");
    }

    public string Protect(string Value) => PostgresConfigurationCrypto.Encrypt(Value, "Shared", "Automation:Railway", Database.EncryptionKey);
    public string Unprotect(string Value) => PostgresConfigurationCrypto.Decrypt(Value, "Shared", "Automation:Railway", Database.EncryptionKey);
}
