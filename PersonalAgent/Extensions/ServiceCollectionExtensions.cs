using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PersonalAgent.Consumers;
using PersonalAgent.Configuration;
using PersonalAgent.Services;

namespace PersonalAgent.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersonalAgentServices(this IServiceCollection services, IConfiguration configuration)
    {
        var securityOptions = BuildSecurityOptions(configuration);

        services.AddMessagingOptions(configuration);
        services.AddAgentMemoryOptions(configuration);
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
            options.CreateInfrastructure = true;
        });

        services.AddOptions<ApiKeyOptions>()
            .Configure(opts =>
            {
                opts.OpenAiKey = ConfigurationValueResolver.ResolveString(configuration, "OPENAI_API_KEY", "OpenAI:ApiKey")
                    ?? string.Empty;
                opts.InternalApiKey = ConfigurationValueResolver.ResolveString(configuration, "INTERNAL_API_KEY", "Security:InternalApiKey")
                    ?? string.Empty;
            });

        services.AddOptions<ChatModelCatalogOptions>()
            .Bind(configuration.GetSection(ChatModelCatalogOptions.SectionName));

        services.AddOptions<SecurityOptions>()
            .Configure(opts => CopySecurityOptions(securityOptions, opts));
        services.AddHostedService<AgentMemorySchemaInitializer>();
        services.AddSingleton<ChatModelCatalog>();
        services.AddSingleton<IAgentSessionStore, PostgresAgentSessionStore>();
        services.AddSingleton<IAgentSemanticMemoryStore>(sp => (PostgresAgentSessionStore)sp.GetRequiredService<IAgentSessionStore>());
        services.AddSingleton<IAgentEmbeddingService, OpenAiAgentEmbeddingService>();
        services.AddSingleton<SemanticMemoryService>();
        services.AddSingleton<AgentEventService>();
        services.AddSingleton<WorkJournalService>();
        services.AddSingleton<AgentChatService>();
        services.AddSingleton<AgentService>();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<TestEventRequestedConsumer>()
                .Endpoint(e =>
                {
                    e.Name = "personal-agent-test-event-requested";
                    e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<TestEventRequested>(_ => { }));
                });

            x.ConfigureSharedPostgresTransport();
        });
        services.AddEndpointsApiExplorer();

        services.AddCors(options =>
        {
            options.AddPolicy(PersonalAgentConstants.ApiCorsPolicy, policy =>
            {
                if (securityOptions.AllowedOrigins.Length is 0) return;
                policy.WithOrigins(securityOptions.AllowedOrigins).AllowAnyMethod().AllowAnyHeader();
            });
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(PersonalAgentConstants.ApiRateLimiter, limiter =>
            {
                limiter.PermitLimit = securityOptions.RateLimit.PermitLimit;
                limiter.Window = TimeSpan.FromSeconds(securityOptions.RateLimit.WindowSeconds);
                limiter.QueueLimit = 0;
            });
        });

        return services;
    }

    private static SecurityOptions BuildSecurityOptions(IConfiguration configuration)
    {
        var options = new SecurityOptions();
        configuration.GetSection("Security").Bind(options);

        var envOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");
        if (!string.IsNullOrWhiteSpace(envOrigins))
            options.AllowedOrigins = envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return options;
    }

    private static void CopySecurityOptions(SecurityOptions source, SecurityOptions destination)
    {
        destination.AllowedOrigins = [.. source.AllowedOrigins];
        destination.InternalApiKey = source.InternalApiKey;
        destination.RateLimit = source.RateLimit with { };
    }
}
