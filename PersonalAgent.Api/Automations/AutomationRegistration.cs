using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Automations;

internal static class AutomationRegistration
{
    public static readonly Uri SagaAddress = new("queue:personal-agent-automation-runs");
    public static readonly Uri StepAddress = new("queue:personal-agent-automation-steps");

    public static IServiceCollection AddAutomations(this IServiceCollection Services)
    {
        Services.AddDbContext<AutomationDbContext>((Provider, Options) => Options.UseNpgsql(
            Provider.GetRequiredService<IOptions<AgentMemoryOptions>>().Value.ConnectionString,
            Npgsql => Npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "automation")));
        Services.AddHostedService<AutomationMigrationService>();
        Services.AddScoped<AutomationService>();
        Services.AddScoped<AutomationSagaActions>();
        Services.AddSingleton<AutomationAuthorization>();
        Services.AddSingleton<AutomationRecipes>();
        Services.AddSingleton<AutomationTools>();
        Services.AddSingleton<PersonalAgent.Integrations.AutomationRuntimeStore>();
        Services.AddSingleton<PersonalAgent.Integrations.AutomationSandboxStore>();
        Services.AddScoped<AutomationOperationGateway>();
        Services.AddHostedService<AutomationRuntimeMigration>();
        Services.AddHostedService<AutomationScheduler>();
        return Services;
    }

    public static void AddAutomationMessaging(this IBusRegistrationConfigurator Bus)
    {
        Bus.AddEntityFrameworkOutbox<AutomationDbContext>(O =>
        {
            O.UsePostgres();
            O.UseBusOutbox();
            O.QueryDelay = TimeSpan.FromSeconds(1);
        });
        Bus.AddSagaStateMachine<AutomationStateMachine, AutomationRun, AutomationSagaDefinition>()
            .EntityFrameworkRepository(R =>
            {
                R.ExistingDbContext<AutomationDbContext>();
                R.ConcurrencyMode = ConcurrencyMode.Pessimistic;
                R.UsePostgres();
            });
        Bus.AddConsumer<AutomationStepConsumer, AutomationStepDefinition>();
    }
}

internal sealed class AutomationRuntimeMigration(PersonalAgent.Integrations.AutomationRuntimeStore Store) : IHostedService
{
    public Task StartAsync(CancellationToken Token) => Store.InitializeAsync(Token);
    public Task StopAsync(CancellationToken Token) => Task.CompletedTask;
}

internal sealed class AutomationSagaDefinition : SagaDefinition<AutomationRun>
{
    public AutomationSagaDefinition() { EndpointName = "personal-agent-automation-runs"; ConcurrentMessageLimit = 8; }
    protected override void ConfigureSaga(IReceiveEndpointConfigurator Endpoint, ISagaConfigurator<AutomationRun> Saga, IRegistrationContext Context)
    {
        Endpoint.UseMessageRetry(R => R.Intervals(100, 500, 1000));
        Endpoint.UseEntityFrameworkOutbox<AutomationDbContext>(Context);
    }
}

internal sealed class AutomationStepDefinition : ConsumerDefinition<AutomationStepConsumer>
{
    public AutomationStepDefinition() { EndpointName = "personal-agent-automation-steps"; ConcurrentMessageLimit = 4; }
    protected override void ConfigureConsumer(IReceiveEndpointConfigurator Endpoint, IConsumerConfigurator<AutomationStepConsumer> Consumer, IRegistrationContext Context)
    {
        Endpoint.UseMessageRetry(R => R.Intervals(100, 500, 1000));
        Endpoint.UseEntityFrameworkOutbox<AutomationDbContext>(Context);
    }
}

internal sealed class AutomationMigrationService(IServiceScopeFactory Scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken Token)
    {
        await using var Scope = Scopes.CreateAsyncScope();
        await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Database.MigrateAsync(Token);
    }
    public Task StopAsync(CancellationToken Token) => Task.CompletedTask;
}

internal sealed class AutomationScheduler(IServiceScopeFactory Scopes, TimeProvider Clock, ILogger<AutomationScheduler> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), Clock, StoppingToken);
                await SweepAsync(StoppingToken);
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { break; }
            catch (Exception Exception) { Logger.LogError(new EventId(4303, "AutomationSchedulerFailed"), Exception, "Automation reconciliation failed with {ExceptionType}", Exception.GetType().Name); }
        }
    }

    internal async Task SweepAsync(CancellationToken Token)
    {
        List<Guid> Due;
        await using (var Scope = Scopes.CreateAsyncScope())
        {
            var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            var Now = Clock.GetUtcNow();
            Due = await Db.Automations.AsNoTracking().Where(D => D.Status == "Active" && D.NextRunAt <= Now)
                .OrderBy(D => D.NextRunAt).Select(D => D.Id).Take(50).ToListAsync(Token);
            // A durable timeout prevents a permanently faulted consumer from leaving a run invisible in Running.
            var Cutoff = Now.AddMinutes(-5);
            var Stale = await Db.Runs.Where(R => R.CurrentState == "Running" && R.StartedAt < Cutoff
                && !Db.Steps.Any(S => S.RunId == R.CorrelationId && S.CompletedAt > Cutoff)).Take(50).ToListAsync(Token);
            var Sender = Scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>();
            foreach (var Run in Stale)
                await (await Sender.GetSendEndpoint(AutomationRegistration.SagaAddress)).Send(
                    new AutomationStepFailed(Run.CorrelationId, Run.StepIndex, "No step progress for five minutes. The automation was paused."), Token);
            await Db.SaveChangesAsync(Token);
        }
        foreach (var Id in Due)
        {
            await using var Scope = Scopes.CreateAsyncScope();
            await Scope.ServiceProvider.GetRequiredService<AutomationService>().StartAsync(Id, null, Token);
        }
    }
}
