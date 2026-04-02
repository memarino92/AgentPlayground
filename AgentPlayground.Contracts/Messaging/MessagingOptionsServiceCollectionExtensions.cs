using AgentPlayground.Contracts.Configuration;
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
                opts.Schema = ConfigurationValueResolver.ResolveString(configuration, "MESSAGING_SCHEMA", $"{MessagingOptions.SectionName}:Schema", opts.Schema) ?? opts.Schema;
                opts.CreateInfrastructure = ConfigurationValueResolver.ResolveBool(
                    configuration,
                    "MESSAGING_CREATE_INFRASTRUCTURE",
                    $"{MessagingOptions.SectionName}:CreateInfrastructure",
                    opts.CreateInfrastructure);
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), $"{MessagingOptions.SectionName}:ConnectionString is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.Schema), $"{MessagingOptions.SectionName}:Schema is required")
            .ValidateOnStart();

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string configuredValue)
    {
        var connectionString = ConfigurationValueResolver.ResolveString(
            configuration,
            "MESSAGING_CONNECTION_STRING",
            $"{MessagingOptions.SectionName}:ConnectionString",
            configuredValue);

        return PostgresConnectionStringNormalizer.Normalize(connectionString)
            ?? PostgresConnectionStringNormalizer.Normalize(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? string.Empty;
    }
}
