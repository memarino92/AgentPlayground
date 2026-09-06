using Microsoft.Extensions.Configuration;
using Npgsql;

namespace AgentPlayground.Contracts.Configuration;

internal sealed class PostgresConfigurationSource(string connectionString, string encryptionKey, string serviceScope) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new PostgresConfigurationProvider(connectionString, encryptionKey, serviceScope);
}

internal sealed class PostgresConfigurationProvider(string connectionString, string encryptionKey, string serviceScope) : ConfigurationProvider
{
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
