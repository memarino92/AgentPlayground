using AgentPlayground.Contracts.Messaging;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AgentPlayground.Integrations;

public sealed class IntegrationDatabase(string ConnectionString, string EncryptionKey)
{
    public string EncryptionKey { get; } = EncryptionKey;
    public bool Available => !string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(EncryptionKey);
    public static IntegrationDatabase FromConfiguration(IConfiguration Configuration) => new(
        PostgresConnectionStringNormalizer.Normalize(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? Configuration["Messaging:ConnectionString"] ?? "",
        Environment.GetEnvironmentVariable("CONFIG_ENCRYPTION_KEY") ?? "");

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken CancellationToken)
    {
        if (!Available) throw new InvalidOperationException("Integration settings require database and encryption bootstrap configuration.");
        var connection = new NpgsqlConnection(ConnectionString);
        try { await connection.OpenAsync(CancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task MigrateAsync(CancellationToken CancellationToken)
    {
        await using var connection = await OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        await using var initialize = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(947120011);
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.integration_schema_versions(version integer PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now());
            """, connection, transaction);
        await initialize.ExecuteNonQueryAsync(CancellationToken);
        await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM app.integration_schema_versions WHERE version = 1)", connection, transaction);
        if ((bool)(await check.ExecuteScalarAsync(CancellationToken))!)
        {
            await transaction.CommitAsync(CancellationToken);
            return;
        }
        await using var command = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS app.integration_heads(
                id text PRIMARY KEY, saved_revision bigint NOT NULL DEFAULT 0, active_revision bigint NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS app.integration_revisions(
                revision bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, integration text NOT NULL,
                encrypted_values text NOT NULL, created_by text NOT NULL, created_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS app.integration_applications(
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, integration text NOT NULL,
                revision bigint NOT NULL, applied_by text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS app.integration_instances(
                integration text NOT NULL, service text NOT NULL, instance text NOT NULL, revision bigint NOT NULL,
                status text NOT NULL, overrides text[] NOT NULL, last_seen timestamptz NOT NULL DEFAULT now(),
                PRIMARY KEY(integration, service, instance));
            INSERT INTO app.integration_heads(id) VALUES ('sentry') ON CONFLICT DO NOTHING;
            INSERT INTO app.integration_schema_versions(version) VALUES (1) ON CONFLICT DO NOTHING;
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(CancellationToken);
        await transaction.CommitAsync(CancellationToken);
    }
}
