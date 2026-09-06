using Microsoft.Extensions.Configuration;
using AgentPlayground.Contracts.Messaging;

namespace AgentPlayground.Contracts.Configuration;

public static class PostgresConfigurationExtensions
{
    public static ConfigurationManager AddPostgresConfiguration(this ConfigurationManager configuration, string serviceScope)
    {
        var rawConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        var encryptionKey = Environment.GetEnvironmentVariable("CONFIG_ENCRYPTION_KEY");
        if (string.IsNullOrWhiteSpace(rawConnectionString) && string.IsNullOrWhiteSpace(encryptionKey)) return configuration;
        if (string.IsNullOrWhiteSpace(rawConnectionString)) throw new InvalidOperationException("DATABASE_URL is required when CONFIG_ENCRYPTION_KEY is configured.");
        if (string.IsNullOrWhiteSpace(encryptionKey)) throw new InvalidOperationException("CONFIG_ENCRYPTION_KEY is required when DATABASE_URL is configured for database-backed configuration.");

        var connectionString = PostgresConnectionStringNormalizer.Normalize(rawConnectionString)
            ?? throw new InvalidOperationException("DATABASE_URL is not a valid PostgreSQL connection string.");
        ((IConfigurationBuilder)configuration).Add(new PostgresConfigurationSource(connectionString, encryptionKey, serviceScope));
        return configuration;
    }
}
