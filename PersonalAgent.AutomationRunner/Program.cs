using MassTransit;
using Microsoft.Extensions.Options;
using PersonalAgent.AutomationRunner;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Contracts.Messaging;
using PersonalAgent.Contracts.Hosting;
using PersonalAgent.Integrations;
using PersonalAgent.Contracts.Coding;

var Builder = Host.CreateApplicationBuilder(args);
Builder.Configuration.AddPostgresConfiguration("AutomationRunner");
Builder.Services.AddRuntimeIntegrations(Builder.Configuration, "AutomationRunner");
Builder.AddApplicationObservability("AutomationRunner");
Builder.Services.AddSingleton<AutomationRuntimeStore>();
Builder.Services.AddSingleton<AutomationSandboxStore>();
Builder.Services.AddSingleton<CodingJobStore>();
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
    Builder.Services.AddHostedService<CodingSandboxReconciler>();
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
    if (!SyntheticEnvironment.IsEnabled(Builder.Configuration, Builder.Environment))
        Bus.AddConsumer<CodingJobConsumer>().Endpoint(E => { E.Name = CodingJobs.Queue; E.ConcurrentMessageLimit = 1; });
    Bus.ConfigureSharedPostgresTransport();
});
await Builder.Build().RunAsync();
