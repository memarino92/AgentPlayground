using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Extensions.Options;
using PersonalAgent.Worker.Consumers;
using PersonalAgent.Worker.Messaging;

var builder = Host.CreateApplicationBuilder(args);

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

    x.ConfigureSharedPostgresTransport();
});

var host = builder.Build();
host.Run();
