using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Events;
using MassTransit;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.Options;
using PersonalAgent.Web.Services;
using PersonalAgent.Web.Consumers;

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
        services.AddSingleton<CoachCallUpdates>();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<CoachCallStatusChangedConsumer>().Endpoint(e =>
            {
                e.Name = $"personal-agent-web-coach-updates-{Guid.NewGuid():N}";
                e.Temporary = true;
                e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<CoachCallStatusChangedEvent>(_ => { }));
            });
            x.ConfigureSharedPostgresTransport();
        });
        return services;
    }
}
