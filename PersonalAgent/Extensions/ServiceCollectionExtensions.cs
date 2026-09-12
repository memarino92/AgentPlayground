using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PersonalAgent.Consumers;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using System.Text;

namespace PersonalAgent.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersonalAgentServices(this IServiceCollection services, IConfiguration configuration)
    {
        var securityOptions = BuildSecurityOptions(configuration);

        services.AddMessagingOptions(configuration);
        services.AddTranscription(configuration);
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
                opts.TavilyApiKey = ConfigurationValueResolver.ResolveString(configuration, "TAVILY_API_KEY", "Tavily:ApiKey")
                    ?? string.Empty;
                opts.TavilyMcpUrl = ConfigurationValueResolver.ResolveString(configuration, "TAVILY_MCP_URL", "Tavily:McpUrl", "https://mcp.tavily.com/mcp")
                    ?? "https://mcp.tavily.com/mcp";
                opts.TavilyDefaultParameters = ConfigurationValueResolver.ResolveString(configuration, "TAVILY_DEFAULT_PARAMETERS", "Tavily:DefaultParameters")
                    ?? string.Empty;
                opts.EnableWebSearch = ConfigurationValueResolver.ResolveBool(configuration, "ENABLE_WEB_SEARCH", "Tavily:EnableWebSearch", true);
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.OpenAiKey), "OpenAI:ApiKey is required")
            .ValidateOnStart();

        services.AddOptions<PushNotificationsOptions>()
            .Configure(opts =>
            {
                opts.Enabled = ConfigurationValueResolver.ResolveBool(configuration, "PUSH_NOTIFICATIONS_ENABLED", $"{PushNotificationsOptions.SectionName}:Enabled", opts.Enabled);
                opts.FirebaseProjectId = ConfigurationValueResolver.ResolveString(configuration, "FIREBASE_PROJECT_ID", $"{PushNotificationsOptions.SectionName}:FirebaseProjectId")
                    ?? string.Empty;
                opts.ServiceAccountJson = ConfigurationValueResolver.ResolveString(configuration, "FIREBASE_SERVICE_ACCOUNT_JSON", $"{PushNotificationsOptions.SectionName}:ServiceAccountJson")
                    ?? string.Empty;
                opts.ServiceAccountJsonBase64 = ConfigurationValueResolver.ResolveString(configuration, "FIREBASE_SERVICE_ACCOUNT_JSON_BASE64", $"{PushNotificationsOptions.SectionName}:ServiceAccountJsonBase64")
                    ?? string.Empty;
                if (string.IsNullOrWhiteSpace(opts.ServiceAccountJson) && !string.IsNullOrWhiteSpace(opts.ServiceAccountJsonBase64))
                    opts.ServiceAccountJson = DecodeBase64Json(opts.ServiceAccountJsonBase64);
                opts.ServiceAccountPath = ConfigurationValueResolver.ResolveString(configuration, "FIREBASE_SERVICE_ACCOUNT_PATH", $"{PushNotificationsOptions.SectionName}:ServiceAccountPath")
                    ?? string.Empty;
                opts.AndroidChannelId = ConfigurationValueResolver.ResolveString(configuration, "ANDROID_PUSH_CHANNEL_ID", $"{PushNotificationsOptions.SectionName}:AndroidChannelId", "agent-approval-high")
                    ?? "agent-approval-high";
            })
            .Validate(opts => !opts.Enabled || !string.IsNullOrWhiteSpace(opts.FirebaseProjectId), $"{PushNotificationsOptions.SectionName}:FirebaseProjectId is required when push notifications are enabled")
            .Validate(opts => !opts.Enabled || !string.IsNullOrWhiteSpace(opts.ServiceAccountJson) || !string.IsNullOrWhiteSpace(opts.ServiceAccountPath), $"{PushNotificationsOptions.SectionName}:ServiceAccountJson or ServiceAccountPath is required when push notifications are enabled")
            .ValidateOnStart();

        services.AddOptions<ChatModelCatalogOptions>()
            .Validate(Options => Options.RefreshIntervalSeconds is >= 1 and <= 86400, "ChatModels:RefreshIntervalSeconds must be between 1 and 86400")
            .Validate(Options => Options.FailureRetrySeconds is >= 1 and <= 3600, "ChatModels:FailureRetrySeconds must be between 1 and 3600")
            .Validate(Options => Options.DiscoveryTimeoutSeconds is >= 1 and <= 60, "ChatModels:DiscoveryTimeoutSeconds must be between 1 and 60")
            .ValidateOnStart();

        services.AddOptions<CoachCheckinOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(CoachCheckinOptions.SectionName).Bind(opts);
                opts.MaxUploadMb = ConfigurationValueResolver.ResolveInt(configuration, "COACH_CHECKINS_MAX_UPLOAD_MB", $"{CoachCheckinOptions.SectionName}:MaxUploadMb", opts.MaxUploadMb, value => value > 0);
                opts.FailedUploadRetentionDays = ConfigurationValueResolver.ResolveInt(configuration, "COACH_CHECKINS_FAILED_UPLOAD_RETENTION_DAYS", $"{CoachCheckinOptions.SectionName}:FailedUploadRetentionDays", opts.FailedUploadRetentionDays, value => value > 0);
            })
            .Validate(opts => opts.MaxUploadMb > 0, $"{CoachCheckinOptions.SectionName}:MaxUploadMb must be greater than zero")
            .Validate(opts => opts.FailedUploadRetentionDays > 0, $"{CoachCheckinOptions.SectionName}:FailedUploadRetentionDays must be greater than zero")
            .ValidateOnStart();

        services.AddOptions<SecurityOptions>()
            .Configure(opts => CopySecurityOptions(securityOptions, opts))
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ActorSigningKey), "Security:ActorSigningKey is required")
            .ValidateOnStart();
        services.AddHostedService<AgentMemorySchemaInitializer>();
        services.AddSingleton(TimeProvider.System);
        services.AddLiveOptions<ApiKeyOptions>(configuration);
        services.AddSingleton<OpenAiClientProvider>();
        services.AddSingleton<IChatModelDiscovery>(sp => new OpenAiChatModelDiscovery(() => sp.GetRequiredService<OpenAiClientProvider>().Current.GetOpenAIModelClient()));
        services.AddSingleton<IChatModelCatalog, ChatModelCatalog>();
        services.AddSingleton<DatabaseChatModelPolicy>();
        services.AddSingleton<IChatModelPolicySource>(sp => sp.GetRequiredService<DatabaseChatModelPolicy>());
        services.AddSingleton<IAgentSessionStore, PostgresAgentSessionStore>();
        services.AddSingleton<IAgentSemanticMemoryStore>(sp => (PostgresAgentSessionStore)sp.GetRequiredService<IAgentSessionStore>());
        services.AddSingleton<IAgentApprovalStore>(sp => (PostgresAgentSessionStore)sp.GetRequiredService<IAgentSessionStore>());
        services.AddSingleton<IAgentEmbeddingService, OpenAiAgentEmbeddingService>();
        services.AddSingleton<SemanticMemoryService>();
        services.AddSingleton<AgentEventService>();
        services.AddSingleton<AgentApprovalService>();
        services.AddSingleton<PushNotificationService>();
        services.AddSingleton<WorkJournalParsingService>();
        services.AddSingleton<IWorkJournalParsingService>(sp => sp.GetRequiredService<WorkJournalParsingService>());
        services.AddSingleton<IAgentChatClientFactory, OpenAiAgentChatClientFactory>();
        services.AddSingleton<WorkJournalService>();
        services.AddSingleton<CoachCheckinService>();
        services.AddSingleton<ICoachEvidenceService, CoachEvidenceService>();
        services.AddSingleton<SchedulingService>();
        services.AddSingleton<ScheduledJobStore>();
        services.AddSingleton<IScheduledActorPolicy, DatabaseScheduledActorPolicy>();
        services.AddSingleton<ScheduledJobAuthorization>();
        services.AddSingleton<IScheduledJobRunner, ScheduledJobRunner>();
        services.AddSingleton<IScheduledNotificationSender, ScheduledNotificationSender>();
        services.AddSingleton<ScheduledJobExecutionService>();
        services.AddHostedService<ScheduledJobReconciler>();
        services.AddSingleton<TavilyMcpToolProvider>();
        services.AddSingleton<ITavilyMcpToolProvider>(sp => sp.GetRequiredService<TavilyMcpToolProvider>());
        services.AddHostedService(sp => sp.GetRequiredService<TavilyMcpToolProvider>());
        services.AddSingleton<IToolAccessStore, PostgresToolAccessStore>();
        services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        services.AddSingleton<AgentToolBinder>();
        services.AddSingleton<ToolAccessService>();
        services.AddSingleton<ICoachAssignmentStore, PostgresCoachAssignmentStore>();
        services.AddSingleton<AgentChatService>();
        services.AddSingleton<AgentService>();
        services.AddMassTransit(x =>
        {
            x.AddConsumer<TranscriptionRequestConsumer>()
                .Endpoint(e => e.Name = "personal-agent-transcription");
            x.AddConsumer<ParseWorkJournalEntriesRequestConsumer>(cfg =>
                cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))))
                .Endpoint(e =>
                {
                    e.Name = "personal-agent-parse-work-journal-entries";
                });
            x.AddConsumer<GenerateEmbeddingsRequestConsumer>(cfg =>
                cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2))))
                .Endpoint(e =>
                {
                    e.Name = "personal-agent-generate-embeddings";
                });
            x.AddConsumer<DevicePushNotificationRequestedConsumer>(cfg =>
                cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2))))
                .Endpoint(e =>
                {
                    e.Name = "personal-agent-device-push-notification-requested";
                    e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<DevicePushNotificationRequested>(_ => { }));
                });
            x.AddConsumer<NotificationSchedulerConsumer>(cfg =>
                cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))))
                .Endpoint(e =>
                {
                    e.Name = MessagingEndpointNames.NotificationScheduler;
                    e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<NotificationRequested>(_ => { }));
                });
            x.AddConsumer<PushNotificationConsumer>(cfg =>
                cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))))
                .Endpoint(e =>
                {
                    e.Name = MessagingEndpointNames.PushNotification;
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

    private static string DecodeBase64Json(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("FIREBASE_SERVICE_ACCOUNT_JSON_BASE64 is not valid base64", ex);
        }
    }

    private static SecurityOptions BuildSecurityOptions(IConfiguration configuration)
    {
        var options = new SecurityOptions();
        configuration.GetSection("Security").Bind(options);
        options.ActorSigningKey = ConfigurationValueResolver.ResolveString(configuration, "WEB_ACTOR_SIGNING_KEY", "Security:ActorSigningKey", options.ActorSigningKey)
            ?? options.ActorSigningKey;

        var envOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");
        if (!string.IsNullOrWhiteSpace(envOrigins))
            options.AllowedOrigins = envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return options;
    }

    private static void CopySecurityOptions(SecurityOptions source, SecurityOptions destination)
    {
        destination.AllowedOrigins = [.. source.AllowedOrigins];
        destination.InternalApiKey = source.InternalApiKey;
        destination.ActorSigningKey = source.ActorSigningKey;
        destination.RateLimit = source.RateLimit with { };
    }
}
