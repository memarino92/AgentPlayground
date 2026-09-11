using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AgentPlayground.Contracts.Configuration;

internal sealed class PostgresConfigurationSource(string connectionString, string encryptionKey, string serviceScope) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new PostgresConfigurationProvider(connectionString, encryptionKey, serviceScope);
}

public sealed class PostgresConfigurationProvider(string connectionString, string encryptionKey, string serviceScope) : ConfigurationProvider
{
    private readonly SemaphoreSlim ReloadGate = new(1, 1);

    public async Task<bool> ReloadCredentialsAsync(CancellationToken CancellationToken)
    {
        if (serviceScope != "Api") return false;
        await ReloadGate.WaitAsync(CancellationToken);
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT scope, key, value, is_secret FROM app.configuration_settings
                WHERE scope IN ('Shared', 'Api') AND is_active AND lower(key) = ANY(@keys)
                ORDER BY CASE WHEN scope = 'Shared' THEN 0 ELSE 1 END, key
                """, connection);
            command.Parameters.AddWithValue("keys", LiveCredentialPolicy.EnvironmentVariables.Keys.Select(Key => Key.ToLowerInvariant()).ToArray());
            var candidate = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await command.ExecuteReaderAsync(CancellationToken))
            {
                while (await reader.ReadAsync(CancellationToken))
                {
                    var scope = reader.GetString(0);
                    var key = reader.GetString(1);
                    candidate[key] = reader.GetBoolean(3)
                        ? PostgresConfigurationCrypto.Decrypt(reader.GetString(2), scope, key, encryptionKey)
                        : reader.GetString(2);
                }
            }
            var values = new Dictionary<string, string?>(Data, StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var (key, environmentVariable) in LiveCredentialPolicy.EnvironmentVariables)
            {
                Data.TryGetValue(key, out var previous);
                candidate.TryGetValue(key, out var next);
                if (previous == next) continue;
                var environmentOverride = Environment.GetEnvironmentVariable(environmentVariable);
                var effective = string.IsNullOrWhiteSpace(environmentOverride) ? next : environmentOverride;
                if (!LiveCredentialPolicy.IsValid(effective))
                    throw new InvalidOperationException("Live provider credentials must be nonempty printable tokens. Current credentials were retained.");
                if (next is null) values.Remove(key);
                else values[key] = next;
                changed = true;
            }
            if (!changed) return false;
            Data = values;
            OnReload();
            return true;
        }
        finally { ReloadGate.Release(); }
    }

    public override void Load()
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT scope, key, value, is_secret
            FROM app.configuration_settings
            WHERE scope IN ('Shared', @serviceScope)
              AND is_active
            ORDER BY CASE WHEN scope = 'Shared' THEN 0 ELSE 1 END, key;
            """;
        command.Parameters.AddWithValue("serviceScope", serviceScope);

        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var scope = reader.GetString(0);
            var key = reader.GetString(1);
            var value = reader.GetString(2);
            values[key] = reader.GetBoolean(3)
                ? PostgresConfigurationCrypto.Decrypt(value, scope, key, encryptionKey)
                : value;
        }

        Data = values;
    }
}
