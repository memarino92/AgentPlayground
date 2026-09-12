using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Contracts.Hosting;
using Npgsql;

namespace PersonalAgent.Development;

internal static class SyntheticConfiguration
{
    public static async Task InitializeAsync()
    {
        var ConnectionString = SyntheticEnvironment.ValidateTarget(Environment.GetEnvironmentVariable("DATABASE_URL") ?? "");
        var Key = Environment.GetEnvironmentVariable("CONFIG_ENCRYPTION_KEY") ?? throw new InvalidOperationException("Demo encryption key is required.");
        await using var Connection = new NpgsqlConnection(ConnectionString);
        await Connection.OpenAsync();
        await using var Transaction = await Connection.BeginTransactionAsync();
        await using var Guard = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(730910);
            DO $$ BEGIN
              IF to_regclass('public.synthetic_demo_marker') IS NULL AND EXISTS (
                SELECT 1 FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              ) THEN RAISE EXCEPTION 'Synthetic demo refuses an existing unmarked database'; END IF;
            END $$;
            CREATE TABLE IF NOT EXISTS public.synthetic_demo_marker (version integer PRIMARY KEY);
            INSERT INTO public.synthetic_demo_marker VALUES (1) ON CONFLICT DO NOTHING;
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.configuration_settings (
                scope text NOT NULL, key text NOT NULL, value text NOT NULL,
                is_secret boolean NOT NULL, is_active boolean NOT NULL DEFAULT true,
                PRIMARY KEY (scope, key));
            """, Connection, Transaction);
        await Guard.ExecuteNonQueryAsync();
        var Settings = new Dictionary<string, string>
        {
            ["Messaging:ConnectionString"] = ConnectionString,
            ["Security:InternalApiKey"] = "synthetic-local-internal-key",
            ["Security:ActorSigningKey"] = "synthetic-local-actor-signing-key",
            ["PersonalAgentApi:InternalApiKey"] = "synthetic-local-internal-key",
            ["PersonalAgentApi:ActorSigningKey"] = "synthetic-local-actor-signing-key",
            ["PersonalAgentApi:BaseUrl"] = "http://personalagent-api:5100",
            ["OpenAI:ApiKey"] = "synthetic-unused-key",
            ["AssemblyAi:ApiKey"] = "synthetic-unused-key",
            ["Tavily:EnableWebSearch"] = "false",
            ["PushNotifications:Enabled"] = "false",
            ["AgentMemory:EnableSemanticMemory"] = "true",
            ["AgentMemory:EmbeddingModel"] = "synthetic-token-hash-v1",
            ["ChatModels:DiscoverFromProvider"] = "false",
            ["Authentication:Schemes:GitHub:AllowedUsers"] = "demo-owner,demo-other",
            ["Authentication:Schemes:Google:AllowedEmails"] = "coach@example.test"
        };
        foreach (var (Name, Value) in Settings)
        {
            await using var Insert = new NpgsqlCommand("""
                INSERT INTO app.configuration_settings (scope, key, value, is_secret)
                VALUES ('Shared', @key, @value, true) ON CONFLICT DO NOTHING
                """, Connection, Transaction);
            Insert.Parameters.AddWithValue("key", Name);
            Insert.Parameters.AddWithValue("value", PostgresConfigurationCrypto.Encrypt(Value, "Shared", Name, Key));
            await Insert.ExecuteNonQueryAsync();
        }
        await Transaction.CommitAsync();
    }
}
