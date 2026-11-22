using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

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
        Services.AddSingleton<PersonalAgent.Integrations.ScratchpadStore>();
        Services.AddSingleton<PersonalAgent.Integrations.SandboxInputStore>();
        Services.AddSingleton<PersonalAgent.Integrations.CodingJobStore>();
        Services.AddScoped<PersonalAgent.Api.Coding.CodingJobService>();
        Services.AddSingleton<PersonalAgent.Api.Coding.CodingJobTools>();
        Services.AddScoped<PersonalAgent.Api.Coding.CodingConversation>();
        Services.AddHttpClient<PersonalAgent.Api.Coding.ICodingPublisher, PersonalAgent.Api.Coding.GitHubCodingPublisher>(C => C.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddHttpClient<PersonalAgent.Api.Coding.CodingModelGateway>(C => C.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        Services.AddHostedService<PersonalAgent.Api.Coding.CodingJobCoordinator>();
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
        Bus.AddSagaStateMachine<CoachCallWorkflowStateMachine, CoachCallWorkflow, CoachCallWorkflowDefinition>()
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
            var ImageCutoff = Now.AddMinutes(-18);
            var Stale = await Db.Runs.Where(R => R.CurrentState == "Running" && R.StartedAt < Cutoff
                && !Db.Steps.Any(S => S.RunId == R.CorrelationId && S.CompletedAt > Cutoff)
                && !Db.Steps.Any(S => S.RunId == R.CorrelationId && S.Index == R.StepIndex && S.Action == "coding_job")
                && (!Db.Steps.Any(S => S.RunId == R.CorrelationId && S.Index == R.StepIndex && S.Action == "dockerfile")
                    || (R.StartedAt < ImageCutoff && !Db.Steps.Any(S => S.RunId == R.CorrelationId && S.CompletedAt > ImageCutoff)))).Take(50).ToListAsync(Token);
            var Sender = Scope.ServiceProvider.GetRequiredService<ISendEndpointProvider>();
            foreach (var Run in Stale)
                await (await Sender.GetSendEndpoint(AutomationRegistration.SagaAddress)).Send(
                    new AutomationStepFailed(Run.CorrelationId, Run.StepIndex, "Step progress deadline expired. The automation was paused."), Token);
            await ReconcileCodingAsync(Db, Scope.ServiceProvider, Sender, Now, Token);
            await Db.SaveChangesAsync(Token);
        }
        foreach (var Id in Due)
        {
            await using var Scope = Scopes.CreateAsyncScope();
            await Scope.ServiceProvider.GetRequiredService<AutomationService>().StartAsync(Id, null, Token);
        }
    }

    private static async Task ReconcileCodingAsync(AutomationDbContext Db, IServiceProvider Services, ISendEndpointProvider Sender,
        DateTimeOffset Now, CancellationToken Token)
    {
        var Pending = await (from Run in Db.Runs.AsNoTracking()
            join Step in Db.Steps on new { RunId = Run.CorrelationId, Index = Run.StepIndex } equals new { Step.RunId, Step.Index }
            join Definition in Db.Automations on Run.AutomationId equals Definition.Id
            where Run.CurrentState == "Running" && Step.Action == "coding_job"
            orderby Run.StartedAt
            select new
            {
                Run.CorrelationId, Run.StepIndex, Definition.ActorId, Definition.SubjectProfileId,
                StepStarted = Db.Steps.Where(S => S.RunId == Run.CorrelationId && S.Index < Run.StepIndex).Max(S => S.CompletedAt)
                    ?? Run.StartedAt
            }).Take(50).ToListAsync(Token);
        if (Pending.Count == 0) return;
        var Coding = Services.GetRequiredService<CodingJobStore>();
        foreach (var Step in Pending)
        {
            var Key = AutomationStepConsumer.CodingRequestKey(Step.CorrelationId, Step.StepIndex);
            var Job = await Coding.FindRequestAsync(Step.ActorId, Key, Token);
            if (Job is not null && Job.Subject == Step.SubjectProfileId && CodingJobs.Terminal(Job.Status))
            {
                await (await Sender.GetSendEndpoint(AutomationRegistration.StepAddress)).Send(
                    new ExecuteAutomationStep(Step.CorrelationId, Step.StepIndex), Token);
                continue;
            }
            var Deadline = Job?.Deadline.AddMinutes(5) ?? Step.StepStarted?.AddMinutes(5);
            if (Deadline > Now || Deadline is null) continue;
            await (await Sender.GetSendEndpoint(AutomationRegistration.SagaAddress)).Send(
                new AutomationStepFailed(Step.CorrelationId, Step.StepIndex,
                    Job is null ? "The coding child did not start before the dispatch deadline."
                        : "The coding child did not report a terminal result before its deadline. Inspect Coding Jobs."), Token);
        }
    }
}
