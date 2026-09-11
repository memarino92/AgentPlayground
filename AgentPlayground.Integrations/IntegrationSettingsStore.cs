using System.Text.Json;
using AgentPlayground.Contracts.Configuration;
using Npgsql;

namespace AgentPlayground.Integrations;

public interface IIntegrationSettingsStore
{
    Task<IntegrationRevision> ReadAsync(bool Active, CancellationToken CancellationToken);
    Task<long> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken);
    Task ApplyAsync(long Revision, string Actor, CancellationToken CancellationToken);
    Task<IReadOnlyList<IntegrationInstanceResponse>> InstancesAsync(CancellationToken CancellationToken);
    Task AcknowledgeAsync(IntegrationInstanceResponse Instance, CancellationToken CancellationToken);
}

public sealed class IntegrationSettingsStore(IntegrationDatabase Database) : IIntegrationSettingsStore
{
    public async Task<IntegrationRevision> ReadAsync(bool Active, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT h.saved_revision, h.active_revision, r.encrypted_values
            FROM app.integration_heads h LEFT JOIN app.integration_revisions r
            ON r.revision = CASE WHEN @active THEN h.active_revision ELSE h.saved_revision END
            WHERE h.id = 'sentry'
            """, connection);
        command.Parameters.AddWithValue("active", Active);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        if (!await reader.ReadAsync(CancellationToken)) throw new InvalidOperationException("Integration schema is not initialized.");
        var values = reader.IsDBNull(2) ? IntegrationRegistry.Defaults() : Decrypt(reader.GetString(2));
        return new(reader.GetInt64(0), reader.GetInt64(1), values);
    }

    public async Task<long> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        var saved = await LockAsync(connection, transaction, CancellationToken);
        if (saved != Request.ExpectedRevision) throw new IntegrationConflictException();
        var values = IntegrationRegistry.Defaults();
        if (saved != 0)
        {
            await using var read = new NpgsqlCommand("SELECT encrypted_values FROM app.integration_revisions WHERE revision = @revision", connection, transaction);
            read.Parameters.AddWithValue("revision", saved);
            values = Decrypt((string)(await read.ExecuteScalarAsync(CancellationToken))!);
        }
        values = IntegrationRegistry.Merge(values, Request.Values);
        var encrypted = PostgresConfigurationCrypto.Encrypt(JsonSerializer.Serialize(values), "Shared", "Integrations:Sentry", Database.EncryptionKey);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO app.integration_revisions(integration, encrypted_values, created_by)
            VALUES ('sentry', @values, @actor) RETURNING revision
            """, connection, transaction);
        insert.Parameters.AddWithValue("values", encrypted);
        insert.Parameters.AddWithValue("actor", Actor);
        var revision = (long)(await insert.ExecuteScalarAsync(CancellationToken))!;
        await using var update = new NpgsqlCommand("UPDATE app.integration_heads SET saved_revision = @revision WHERE id = 'sentry'", connection, transaction);
        update.Parameters.AddWithValue("revision", revision);
        await update.ExecuteNonQueryAsync(CancellationToken);
        await transaction.CommitAsync(CancellationToken);
        return revision;
    }

    public async Task ApplyAsync(long Revision, string Actor, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        var saved = await LockAsync(connection, transaction, CancellationToken);
        if (saved != Revision || Revision == 0) throw new IntegrationConflictException();
        await using var read = new NpgsqlCommand("SELECT encrypted_values FROM app.integration_revisions WHERE revision = @revision", connection, transaction);
        read.Parameters.AddWithValue("revision", Revision);
        IntegrationRegistry.Validate(Decrypt((string)(await read.ExecuteScalarAsync(CancellationToken))!));
        await using var update = new NpgsqlCommand("""
            UPDATE app.integration_heads SET active_revision = @revision WHERE id = 'sentry';
            INSERT INTO app.integration_applications(integration, revision, applied_by) VALUES ('sentry', @revision, @actor)
            """, connection, transaction);
        update.Parameters.AddWithValue("revision", Revision);
        update.Parameters.AddWithValue("actor", Actor);
        await update.ExecuteNonQueryAsync(CancellationToken);
        await transaction.CommitAsync(CancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationInstanceResponse>> InstancesAsync(CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT service, instance, revision, status, overrides, last_seen FROM app.integration_instances
            WHERE integration = 'sentry' ORDER BY service, last_seen DESC LIMIT 100
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var instances = new List<IntegrationInstanceResponse>();
        while (await reader.ReadAsync(CancellationToken)) instances.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetFieldValue<string[]>(4), reader.GetFieldValue<DateTimeOffset>(5)));
        return instances;
    }

    public async Task AcknowledgeAsync(IntegrationInstanceResponse Instance, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO app.integration_instances(integration, service, instance, revision, status, overrides)
            VALUES ('sentry', @service, @instance, @revision, @status, @overrides)
            ON CONFLICT(integration, service, instance) DO UPDATE SET
            revision = excluded.revision, status = excluded.status, overrides = excluded.overrides, last_seen = now()
            """, connection);
        command.Parameters.AddWithValue("service", Instance.Service);
        command.Parameters.AddWithValue("instance", Instance.Instance);
        command.Parameters.AddWithValue("revision", Instance.Revision);
        command.Parameters.AddWithValue("status", Instance.Status);
        command.Parameters.AddWithValue("overrides", Instance.Overrides);
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    private Dictionary<string, string> Decrypt(string Encrypted) => JsonSerializer.Deserialize<Dictionary<string, string>>(
        PostgresConfigurationCrypto.Decrypt(Encrypted, "Shared", "Integrations:Sentry", Database.EncryptionKey))!;

    private static async Task<long> LockAsync(NpgsqlConnection Connection, NpgsqlTransaction Transaction, CancellationToken CancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT saved_revision FROM app.integration_heads WHERE id = 'sentry' FOR UPDATE", Connection, Transaction);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
}
