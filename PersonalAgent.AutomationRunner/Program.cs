using MassTransit;
using Microsoft.Extensions.Options;
using PersonalAgent.AutomationRunner;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Contracts.Messaging;
using PersonalAgent.Integrations;

var Builder = Host.CreateApplicationBuilder(args);
Builder.Configuration.AddPostgresConfiguration("AutomationRunner");
Builder.Services.AddRuntimeIntegrations(Builder.Configuration, "AutomationRunner");
Builder.AddApplicationObservability("AutomationRunner");
Builder.Services.AddSingleton<DockerProgramExecutor>();
Builder.Services.AddHostedService<SandboxStartup>();
Builder.Services.AddMessagingOptions(Builder.Configuration);
Builder.Services.AddOptions<SqlTransportOptions>().Configure<IOptions<MessagingOptions>>((Sql, Messaging) =>
    Sql.ConnectionString = Messaging.Value.ConnectionString);
Builder.Services.AddPostgresMigrationHostedService(O =>
{
    O.CreateDatabase = false;
    O.CreateSchema = true;
    O.CreateInfrastructure = true;
});
Builder.Services.AddMassTransit(Bus =>
{
    Bus.AddConsumer<ProgramConsumer>().Endpoint(E => { E.Name = AutomationPrograms.Queue; E.ConcurrentMessageLimit = 2; });
    Bus.ConfigureSharedPostgresTransport();
});
await Builder.Build().RunAsync();
