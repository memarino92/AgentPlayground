using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Options;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Extensions;

internal static class ServiceCollectionMessagingExtensions
{
    public static IServiceCollection AddPersonalAgentWebMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMessagingOptions(configuration);
        services.AddOptions<SqlTransportOptions>()
            .Configure<IOptions<MessagingOptions>>((sqlOptions, messagingOptions) =>
            {
                sqlOptions.ConnectionString = messagingOptions.Value.ConnectionString;
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), "SqlTransportOptions:ConnectionString is required")
            .ValidateOnStart();

        services.AddPostgresMigrationHostedService(options =>
        {
            options.CreateDatabase = false;
            options.CreateSchema = true;
            options.CreateInfrastructure = false;
        });

        services.AddScoped<ProtectedSessionStorage>();
        services.AddMassTransit(x => x.ConfigureSharedPostgresTransport());
        return services;
    }
}
