using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlayground.Contracts.Messaging;

public static class MessagingOptionsServiceCollectionExtensions
{
    public static IServiceCollection AddMessagingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MessagingOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(MessagingOptions.SectionName).Bind(opts);

                opts.ConnectionString = ResolveConnectionString(configuration, opts.ConnectionString);
                opts.Schema = Environment.GetEnvironmentVariable("MESSAGING_SCHEMA")
                    ?? configuration[$"{MessagingOptions.SectionName}:Schema"]
                    ?? opts.Schema;

                var createInfrastructure = Environment.GetEnvironmentVariable("MESSAGING_CREATE_INFRASTRUCTURE")
                    ?? configuration[$"{MessagingOptions.SectionName}:CreateInfrastructure"];

                if (bool.TryParse(createInfrastructure, out var shouldCreateInfrastructure))
                    opts.CreateInfrastructure = shouldCreateInfrastructure;
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), $"{MessagingOptions.SectionName}:ConnectionString is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.Schema), $"{MessagingOptions.SectionName}:Schema is required")
            .ValidateOnStart();

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string configuredValue)
    {
        var connectionString = Environment.GetEnvironmentVariable("MESSAGING_CONNECTION_STRING")
            ?? configuration[$"{MessagingOptions.SectionName}:ConnectionString"]
            ?? configuredValue;

        return PostgresConnectionStringNormalizer.Normalize(connectionString)
            ?? PostgresConnectionStringNormalizer.Normalize(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? string.Empty;
    }
}
