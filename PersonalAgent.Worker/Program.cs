using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Events;
using AgentPlayground.Contracts.Messaging.Requests;
using MassTransit;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Consumers;
using PersonalAgent.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddHttpClient("GitHubWorkJournal", client =>
{
    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PersonalAgent", "1.0"));
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddPersonalAgentApiOptions(builder.Configuration);
builder.Services.AddAssemblyAiOptions(builder.Configuration);
builder.Services.AddCoachCheckinWorkerOptions(builder.Configuration);
builder.Services.AddHttpClient("PersonalAgentApi", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<PersonalAgentApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(60);
    if (!string.IsNullOrWhiteSpace(options.InternalApiKey))
        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", options.InternalApiKey);
});
builder.Services.AddHttpClient("AssemblyAi", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<AssemblyAiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromMinutes(options.TranscriptionTimeoutMinutes + 2);
    client.DefaultRequestHeaders.Add("Authorization", options.ApiKey);
});
builder.Services.AddSingleton<IAgentTaskExecutionService, AgentTaskExecutionService>();
builder.Services.AddSingleton<ITranscriptionService, AssemblyAiTranscriptionService>();
builder.Services.AddScoped<CoachTranscriptProcessingService>();
builder.Services.AddHostedService<CoachCallCleanupService>();

var workJournalConfigValidation = WorkerExtensions.ValidateWorkJournalSyncConfiguration(builder.Configuration);
var workJournalSyncEnabled = workJournalConfigValidation.IsValid;

if (workJournalSyncEnabled)
{
    builder.Services.AddGitHubOptions(builder.Configuration);
    builder.Services.AddHostedService<WeeklyWorkJournalSyncService>();
}

builder.Services.AddMessagingOptions(builder.Configuration);
builder.Services.AddOptions<SqlTransportOptions>()
    .Configure<IOptions<MessagingOptions>>((sqlOptions, messagingOptions) =>
    {
        sqlOptions.ConnectionString = messagingOptions.Value.ConnectionString;
    })
    .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), "SqlTransportOptions:ConnectionString is required")
    .ValidateOnStart();

builder.Services.AddPostgresMigrationHostedService(options =>
{
    options.CreateDatabase = false;
    options.CreateSchema = true;
    options.CreateInfrastructure = false;
});
builder.Services.AddMassTransit(x =>
{
    x.AddRequestClient<GenerateEmbeddingsRequest>();

    if (workJournalSyncEnabled)
        x.AddRequestClient<ParseWorkJournalEntriesRequest>();

    x.AddConsumer<TestEventRequestedConsumer>()
        .Endpoint(e =>
        {
            e.Name = "personal-agent-worker-test-event-requested";
            e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<TestEventRequested>(_ => { }));
        });
    x.AddConsumer<AgentGeneratedTestMessageConsumer>()
        .Endpoint(e =>
        {
            e.Name = "personal-agent-worker-agent-generated-test-message";
            e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<AgentGeneratedTestMessage>(_ => { }));
        });
    x.AddConsumer<AgentTaskSchedulerConsumer>(cfg =>
        cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))))
        .Endpoint(e =>
        {
            e.Name = MessagingEndpointNames.AgentTaskScheduler;
            e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<AgentTaskScheduled>(_ => { }));
        });
    x.AddConsumer<AgentTaskExecutorConsumer>(cfg =>
        cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1))))
        .Endpoint(e =>
        {
            e.Name = MessagingEndpointNames.AgentTaskExecutor;
        });

    x.AddConsumer<TranscribeCoachCallConsumer>(cfg =>
        cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2))))
        .Endpoint(e =>
        {
            e.Name = MessagingEndpointNames.CoachCallTranscription;
        });
    x.AddConsumer<ProcessCoachTranscriptConsumer>(cfg =>
        cfg.UseMessageRetry(retry => retry.Exponential(3, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(2))))
        .Endpoint(e =>
        {
            e.Name = MessagingEndpointNames.CoachCallProcessing;
        });
        
    if (workJournalSyncEnabled)
    {
        x.AddConsumer<SyncWorkJournalConsumer>()
            .Endpoint(e =>
            {
                e.Name = "personal-agent-worker-sync-work-journal";
                e.AddSqlConfigureEndpointCallback((_, cfg) => cfg.Subscribe<SyncWorkJournalCommand>(_ => { }));
            });
    }

    x.ConfigureSharedPostgresTransport();
});

var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PersonalAgent.Worker.Startup");
if (workJournalSyncEnabled)
{
    startupLogger.LogInformation("Work journal sync is enabled");
}
else
{
    startupLogger.LogWarning(
        "Work journal sync is disabled due to missing configuration values: {MissingSettings}",
        string.Join(", ", workJournalConfigValidation.MissingSettings));
}

host.Run();
