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

public class IntegrationSettingsStore(IntegrationDatabase Database, string IntegrationId = "sentry") : IIntegrationSettingsStore
{
    public async Task<IntegrationRevision> ReadAsync(bool Active, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = Command("""
            SELECT h.saved_revision, h.active_revision, r.encrypted_values
            FROM app.integration_heads h LEFT JOIN app.integration_revisions r
            ON r.integration = h.id AND r.revision = CASE WHEN @active THEN h.active_revision ELSE h.saved_revision END
            WHERE h.id = @integration
            """, connection);
        command.Parameters.AddWithValue("active", Active);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        if (!await reader.ReadAsync(CancellationToken)) throw new InvalidOperationException("Integration schema is not initialized.");
        var values = reader.IsDBNull(2) ? Defaults() : Decrypt(reader.GetString(2));
        return new(reader.GetInt64(0), reader.GetInt64(1), values);
    }

    public async Task<long> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        var saved = await LockAsync(connection, transaction, CancellationToken);
        if (saved != Request.ExpectedRevision) throw new IntegrationConflictException();
        var values = Defaults();
        if (saved != 0)
        {
            await using var read = Command("SELECT encrypted_values FROM app.integration_revisions WHERE integration = @integration AND revision = @revision", connection, transaction);
            read.Parameters.AddWithValue("revision", saved);
            values = Decrypt((string)(await read.ExecuteScalarAsync(CancellationToken))!);
        }
        values = Merge(values, Request.Values);
        var encrypted = PostgresConfigurationCrypto.Encrypt(JsonSerializer.Serialize(values), "Shared", EncryptionScope, Database.EncryptionKey);
        await using var insert = Command("""
            INSERT INTO app.integration_revisions(integration, encrypted_values, created_by)
            VALUES (@integration, @values, @actor) RETURNING revision
            """, connection, transaction);
        insert.Parameters.AddWithValue("values", encrypted);
        insert.Parameters.AddWithValue("actor", Actor);
        var revision = (long)(await insert.ExecuteScalarAsync(CancellationToken))!;
        await using var update = Command("UPDATE app.integration_heads SET saved_revision = @revision WHERE id = @integration", connection, transaction);
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
        await using var read = Command("SELECT encrypted_values FROM app.integration_revisions WHERE integration = @integration AND revision = @revision", connection, transaction);
        read.Parameters.AddWithValue("revision", Revision);
        Validate(Decrypt((string)(await read.ExecuteScalarAsync(CancellationToken))!));
        await using var update = Command("""
            UPDATE app.integration_heads SET active_revision = @revision WHERE id = @integration;
            INSERT INTO app.integration_applications(integration, revision, applied_by) VALUES (@integration, @revision, @actor)
            """, connection, transaction);
        update.Parameters.AddWithValue("revision", Revision);
        update.Parameters.AddWithValue("actor", Actor);
        await update.ExecuteNonQueryAsync(CancellationToken);
        await transaction.CommitAsync(CancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationInstanceResponse>> InstancesAsync(CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = Command("""
            SELECT service, instance, revision, status, overrides, last_seen FROM app.integration_instances
            WHERE integration = @integration ORDER BY service, last_seen DESC LIMIT 100
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        var instances = new List<IntegrationInstanceResponse>();
        while (await reader.ReadAsync(CancellationToken)) instances.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetFieldValue<string[]>(4), reader.GetFieldValue<DateTimeOffset>(5)));
        return instances;
    }

    public async Task AcknowledgeAsync(IntegrationInstanceResponse Instance, CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = Command("""
            INSERT INTO app.integration_instances(integration, service, instance, revision, status, overrides)
            VALUES (@integration, @service, @instance, @revision, @status, @overrides)
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
        PostgresConfigurationCrypto.Decrypt(Encrypted, "Shared", EncryptionScope, Database.EncryptionKey))!;

    private async Task<long> LockAsync(NpgsqlConnection Connection, NpgsqlTransaction Transaction, CancellationToken CancellationToken)
    {
        await using var command = Command("SELECT saved_revision FROM app.integration_heads WHERE id = @integration FOR UPDATE", Connection, Transaction);
        return (long)(await command.ExecuteScalarAsync(CancellationToken))!;
    }
    private string EncryptionScope => IntegrationId == "sentry" ? "Integrations:Sentry" : "Integrations:OpenTelemetry";
    private Dictionary<string, string> Defaults() => IntegrationId == "sentry" ? IntegrationRegistry.Defaults() : OtelSettings.Defaults();
    private Dictionary<string, string> Merge(Dictionary<string, string> Saved, Dictionary<string, string> Changes)
        => IntegrationId == "sentry" ? IntegrationRegistry.Merge(Saved, Changes) : OtelSettings.Merge(Saved, Changes);
    private void Validate(IReadOnlyDictionary<string, string> Values)
    {
        if (IntegrationId == "sentry") IntegrationRegistry.Validate(Values);
        else OtelSettings.Validate(Values);
    }
    private NpgsqlCommand Command(string Sql, NpgsqlConnection Connection, NpgsqlTransaction? Transaction = null)
    {
        var command = new NpgsqlCommand(Sql, Connection, Transaction);
        command.Parameters.AddWithValue("integration", IntegrationId);
        return command;
    }}

public interface IOtelSettingsStore : IIntegrationSettingsStore;
public sealed class OtelSettingsStore(IntegrationDatabase Database) : IntegrationSettingsStore(Database, "otel"), IOtelSettingsStore;