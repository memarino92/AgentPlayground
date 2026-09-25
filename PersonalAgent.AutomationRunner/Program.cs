using MassTransit;
using Microsoft.Extensions.Options;
using PersonalAgent.AutomationRunner;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Contracts.Messaging;
using PersonalAgent.Contracts.Hosting;
using PersonalAgent.Integrations;

var Builder = Host.CreateApplicationBuilder(args);
Builder.Configuration.AddPostgresConfiguration("AutomationRunner");
Builder.Services.AddRuntimeIntegrations(Builder.Configuration, "AutomationRunner");
Builder.AddApplicationObservability("AutomationRunner");
Builder.Services.AddSingleton<AutomationRuntimeStore>();
Builder.Services.AddSingleton<AutomationSandboxStore>();
if (SyntheticEnvironment.IsEnabled(Builder.Configuration, Builder.Environment))
{
    Builder.Services.AddSingleton<DockerProgramExecutor>();
    Builder.Services.AddSingleton<IProgramExecutor>(S => S.GetRequiredService<DockerProgramExecutor>());
    Builder.Services.AddHostedService<SandboxStartup>();
}
else
{
    Builder.Services.AddSingleton<IRailwaySandboxClient, RailwaySandboxClient>();
    Builder.Services.AddSingleton<IProgramExecutor, RailwayProgramExecutor>();
    Builder.Services.AddHostedService<SandboxReconciler>();
}
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
