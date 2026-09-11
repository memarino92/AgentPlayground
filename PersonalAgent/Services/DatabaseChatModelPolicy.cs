using System.Text.Json;
using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Integrations;
using Npgsql;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal interface IChatModelPolicySource
{
    Task<ChatModelCatalogOptions> ReadAsync(CancellationToken CancellationToken);
}

internal sealed class DatabaseChatModelPolicy(IntegrationDatabase Database) : IChatModelPolicySource
{
    public async Task InitializeAsync(CancellationToken CancellationToken)
    {
        await using var Connection = await Database.OpenAsync(CancellationToken);
        await using var Transaction = await Connection.BeginTransactionAsync(CancellationToken);
        await using var Command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(947120016);
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.configuration_settings (
                scope text NOT NULL, key text NOT NULL, value text NOT NULL,
                is_secret boolean NOT NULL, is_active boolean NOT NULL DEFAULT true,
                updated_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(scope, key));
            """, Connection, Transaction);
        await Command.ExecuteNonQueryAsync(CancellationToken);
        Command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM app.configuration_settings
                WHERE scope IN ('Shared', 'Api') AND
                    (lower(key) LIKE 'chatmodels:models:%' OR lower(key) = 'chatmodels:policyinitialized'))
            """;
        var HasModels = (bool)(await Command.ExecuteScalarAsync(CancellationToken))!;
        Command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'app' AND table_name = 'configuration_settings' AND column_name = 'updated_at')
            """;
        var HasTimestamp = (bool)(await Command.ExecuteScalarAsync(CancellationToken))!;
        var Defaults = LoadSeed();
        var Values = new Dictionary<string, string>
        {
            ["DiscoverFromProvider"] = "true", ["RefreshIntervalSeconds"] = "300",
            ["FailureRetrySeconds"] = "30", ["DiscoveryTimeoutSeconds"] = "5", ["PolicyInitialized"] = "true"
        };
        if (!HasModels)
            for (var Index = 0; Index < Defaults.Models.Count; Index++)
            {
                var Model = Defaults.Models[Index];
                Values[$"Models:{Index}:Id"] = Model.Id;
                Values[$"Models:{Index}:DisplayName"] = Model.DisplayName;
                Values[$"Models:{Index}:IsDefault"] = Model.IsDefault.ToString();
            }
        foreach (var (Key, Value) in Values)
        {
            Command.CommandText = $"""
                INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active{(HasTimestamp ? ", updated_at" : "")})
                SELECT 'Api', @key, @value, false, true{(HasTimestamp ? ", now()" : "")}
                WHERE NOT EXISTS(SELECT 1 FROM app.configuration_settings
                    WHERE scope IN ('Shared', 'Api') AND lower(key) = lower(@key))
                ON CONFLICT DO NOTHING
                """;
            Command.Parameters.Clear();
            Command.Parameters.AddWithValue("key", $"ChatModels:{Key}");
            Command.Parameters.AddWithValue("value", Value);
            await Command.ExecuteNonQueryAsync(CancellationToken);
        }
        await Transaction.CommitAsync(CancellationToken);
    }

    public async Task<ChatModelCatalogOptions> ReadAsync(CancellationToken CancellationToken)
    {
        await using var Connection = await Database.OpenAsync(CancellationToken);
        await using var Command = new NpgsqlCommand("""
            SELECT scope, key, value, is_secret FROM app.configuration_settings
            WHERE scope IN ('Shared', 'Api') AND is_active AND lower(key) LIKE 'chatmodels:%'
            ORDER BY CASE WHEN scope = 'Shared' THEN 0 ELSE 1 END, key
            """, Connection);
        var Values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await using var Reader = await Command.ExecuteReaderAsync(CancellationToken);
        while (await Reader.ReadAsync(CancellationToken))
        {
            var Scope = Reader.GetString(0);
            var Key = Reader.GetString(1);
            Values[Key] = Reader.GetBoolean(3)
                ? PostgresConfigurationCrypto.Decrypt(Reader.GetString(2), Scope, Key, Database.EncryptionKey)
                : Reader.GetString(2);
        }
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(Values).Build();
        using var ConfigurationLifetime = Configuration as IDisposable;
        ChatModelCatalogOptions Policy;
        try { Policy = Configuration.GetSection(ChatModelCatalogOptions.SectionName).Get<ChatModelCatalogOptions>() ?? new(); }
        catch (InvalidOperationException) { throw new InvalidOperationException("Invalid database chat model policy; check ChatModels settings."); }
        if (Policy.RefreshIntervalSeconds is < 1 or > 86400 || Policy.FailureRetrySeconds is < 1 or > 3600
            || Policy.DiscoveryTimeoutSeconds is < 1 or > 60)
            throw new InvalidOperationException("Invalid database chat model policy; check discovery intervals.");
        return Policy;
    }

    internal static ChatModelCatalogOptions LoadSeed()
    {
        using var Stream = typeof(DatabaseChatModelPolicy).Assembly.GetManifestResourceStream("PersonalAgent.Configuration.chat-model-policy.seed.json")!;
        return JsonSerializer.Deserialize<ChatModelCatalogOptions>(Stream)!;
    }
}

internal sealed class DatabaseChatModelPolicyInitializer(DatabaseChatModelPolicy Policy) : IHostedService
{
    public Task StartAsync(CancellationToken CancellationToken) => Policy.InitializeAsync(CancellationToken);
    public Task StopAsync(CancellationToken CancellationToken) => Task.CompletedTask;
}
