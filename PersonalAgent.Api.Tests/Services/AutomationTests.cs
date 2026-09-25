using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Messaging.Events;
using PersonalAgent.Integrations;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed partial class AutomationTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    private static readonly AgentAccessContext Owner = new("owner", AgentRoles.Owner, "owner");
    private const string Source = """
        {"steps":[{"id":"message","action":"text","arguments":{"text":"Synthetic report"}},
        {"id":"report","action":"save_report","arguments":{"title":"Weekly report","content":"{{steps.message}}"}}]}
        """;

    [Fact]
    public async Task DurableOutboxStartsAfterHostRestart_AndDuplicatesDoNotRepeatDomainWrites()
    {
        using var Host = await CreateHostAsync();
        Guid Id;
        Guid RunId;
        await using (var Scope = Host.Services.CreateAsyncScope())
        {
            var Service = Scope.ServiceProvider.GetRequiredService<AutomationService>();
            Id = (await Service.SaveAsync(Owner, null, new("Report", Source), default)).Id;
            RunId = (await Service.StartAsync(Id, null, default))!.Value;
            var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            (await Db.Set<OutboxMessage>().CountAsync()).Should().Be(1);
        }
        // Build a fresh host: the only handoff is persisted PostgreSQL state/outbox, not process memory.
        using var Restarted = await CreateHostAsync(Reset: false);
        await Restarted.StartAsync();
        try
        {
            await WaitAsync(Restarted, RunId, "Completed");
            var Bus = Restarted.Services.GetRequiredService<IBus>();
            var Endpoint = await Bus.GetSendEndpoint(AutomationRegistration.SagaAddress);
            await Endpoint.Send(new StartAutomation(RunId));
            await Endpoint.Send(new AutomationStepCompleted(RunId, 0, "forged duplicate"));
            await Endpoint.Send(new AutomationStepCompleted(RunId, 1, "forged duplicate"));
            await Task.Delay(300);
            await using var Scope = Restarted.Services.CreateAsyncScope();
            var Detail = await Scope.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, RunId, default);
            Detail.Steps.Should().HaveCount(2).And.OnlyContain(S => S.Status == "Completed");
            Detail.Reports.Should().ContainSingle().Which.Content.Should().Be("Synthetic report");
        }
        finally { await Restarted.StopAsync(); }
    }

    [Fact]
    public async Task DomainSagaAndOutboxRollbackTogether_ThenRecoverWithoutDuplicateReport()
    {
        var Failure = new FailReportCommit { Enabled = true };
        using var Host = await CreateHostAsync(Failure: Failure);
        var Source = """{"steps":[{"id":"report","action":"save_report","arguments":{"title":"Report","content":"Atomic"}},{"id":"next","action":"text","arguments":{"text":"Done"}}]}""";
        var (Id, Run) = await SaveAndQueueAsync(Host, Source);
        await Host.StartAsync();
        try
        {
            await UntilAsync(() => Task.FromResult(Failure.Attempts >= 4));
            await using (var Scope = Host.Services.CreateAsyncScope())
            {
                var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
                (await Db.Reports.CountAsync()).Should().Be(0);
                var State = await Db.Runs.SingleAsync(R => R.CorrelationId == Run);
                State.StepIndex.Should().Be(0);
                State.CurrentState.Should().Be("Running");
                (await Db.Steps.SingleAsync(S => S.RunId == Run && S.Index == 0)).Status.Should().Be("Pending");
            }
            Failure.Enabled = false;
            await (await Host.Services.GetRequiredService<IBus>().GetSendEndpoint(AutomationRegistration.SagaAddress))
                .Send(new AutomationStepCompleted(Run, 0, "{}"));
            await WaitAsync(Host, Run, "Completed");
            await using var Check = Host.Services.CreateAsyncScope();
            var Detail = await Check.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Reports.Should().ContainSingle().Which.Content.Should().Be("Atomic");
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public async Task RevocationBlocksExecution_AndCrossSubjectAccessIsDenied()
    {
        var Policy = PolicyMock();
        using var Host = await CreateHostAsync(Policy);
        var (Id, Run) = await SaveAndQueueAsync(Host, Source);
        await using (var Scope = Host.Services.CreateAsyncScope())
        {
            var Service = Scope.ServiceProvider.GetRequiredService<AutomationService>();
            await FluentActions.Awaiting(() => Service.DetailAsync(new("other", "Owner", "owner"), Id, 0, default)).Should().ThrowAsync<UnauthorizedAccessException>();
            await FluentActions.Awaiting(() => Service.DetailAsync(new("other", "Owner", "other"), Id, 0, default)).Should().ThrowAsync<KeyNotFoundException>();
        }
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        await Host.StartAsync();
        try
        {
            await WaitAsync(Host, Run, "Blocked");
            await using var Scope = Host.Services.CreateAsyncScope();
            var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            (await Db.Reports.CountAsync()).Should().Be(0);
            (await Db.Automations.SingleAsync(D => D.Id == Id)).Status.Should().Be("Paused");
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public async Task VersionsArePinned_ConcurrentSchedulingCreatesOneRun_AndPausePreservesHistory()
    {
        using var Host = await CreateHostAsync();
        Guid Id;
        await using (var Scope = Host.Services.CreateAsyncScope())
            Id = (await Scope.ServiceProvider.GetRequiredService<AutomationService>().SaveAsync(Owner, null,
                new("Recurring report", Source, DateTimeOffset.UtcNow.AddHours(-3), "PT1H"), default)).Id;
        async Task<Guid?> Start()
        {
            await using var Scope = Host.Services.CreateAsyncScope();
            return await Scope.ServiceProvider.GetRequiredService<AutomationService>().StartAsync(Id, null, default);
        }
        var Starts = await Task.WhenAll(Start(), Start());
        Starts.Count(R => R is not null).Should().Be(1);
        var Run = Starts.Single(R => R is not null)!.Value;
        await using (var Scope = Host.Services.CreateAsyncScope())
        {
            var Service = Scope.ServiceProvider.GetRequiredService<AutomationService>();
            var Detail = await Service.DetailAsync(Owner, Id, 0, default);
            Detail.Automation.NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
            Detail.UpcomingRuns.Should().HaveCount(5);
            await Service.SaveAsync(Owner, Id, new("Updated", Source.Replace("Synthetic report", "Version two"), DateTimeOffset.UtcNow.AddDays(1), "P1D", 1), default);
            await FluentActions.Awaiting(() => Service.SaveAsync(Owner, Id, new("Stale", Source, ExpectedVersion: 1), default)).Should().ThrowAsync<InvalidOperationException>();
        }
        await Host.StartAsync();
        try
        {
            await WaitAsync(Host, Run, "Completed");
            await using var Scope = Host.Services.CreateAsyncScope();
            var Service = Scope.ServiceProvider.GetRequiredService<AutomationService>();
            var Detail = await Service.RunDetailAsync(Owner, Id, Run, default);
            Detail.Run.Version.Should().Be(1);
            Detail.Reports.Single().Content.Should().Be("Synthetic report");
            await Service.SetStatusAsync(Owner, Id, true, default);
            var Definition = await Service.DetailAsync(Owner, Id, 0, default);
            Definition.Versions.Should().HaveCount(2);
            Definition.UpcomingRuns.Should().BeEmpty();
            Definition.Runs.Should().ContainSingle();
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public async Task RegisteredClockConditionalStepAndNotificationUseAuthorizedActions()
    {
        using var Host = await CreateHostAsync();
        var Recipe = """
            {"steps":[
              {"id":"clock","action":"tool","arguments":{"tool":"Local:get_current_date_time","inputs":{"timeZoneId":"UTC"}}},
              {"id":"skip","action":"save_report","arguments":{"title":"Skipped","content":"Should not be saved"},"when":{"step":"clock","equals":"impossible"}},
              {"id":"notice","action":"notify","arguments":{"title":"Time","body":"{{steps.clock}}"}}
            ]}
            """;
        var (Id, Run) = await SaveAndQueueAsync(Host, Recipe);
        await Host.StartAsync();
        try
        {
            await WaitAsync(Host, Run, "Completed");
            var Capture = Host.Services.GetRequiredService<NotificationCapture>();
            await UntilAsync(() => Task.FromResult(Capture.Items.Count == 1));
            Capture.Items.Single().ProfileId.Should().Be("owner");
            Capture.Items.Single().Body.Should().Contain("Current UTC date and time");
            await using var Scope = Host.Services.CreateAsyncScope();
            var Detail = await Scope.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Steps[1].Status.Should().Be("Skipped");
            Detail.Reports.Should().BeEmpty();
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public void RecipeRejectsCodeUnknownActionsForwardReferencesAndUnboundedGraphs()
    {
        var Recipes = new AutomationRecipes(new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<AIFunction>())));
        foreach (var Source in new[]
        {
            """{"steps":[{"id":"a","action":"shell","arguments":{}}]}""",
            """{"steps":[{"id":"a","action":"text","arguments":{"text":"{{steps.later}}"}}]}""",
            """{"steps":[{"id":"a","action":"tool","arguments":{"tool":"Local:save_automation","inputs":{}}}]}""",
            """{"steps":[{"id":"a","action":"text","arguments":{"text":"x","text":"y"}}]}""",
            """{"steps":null}"""
        }) FluentActions.Invoking(() => Recipes.Parse(Source)).Should().Throw<ArgumentException>();
        var Recipe = Recipes.Parse("""{"steps":[{"id":"a","action":"text","arguments":{"text":"quote: {{steps.other}}"}}]}""".Replace("{{steps.other}}", "plain"));
        Recipe.Steps.Should().ContainSingle();
    }

    [Fact]
    public async Task CSharpIsOptIn_DispatchesLiteralSource_AndRevocationBlocksResultCommit()
    {
        var Grants = new Dictionary<string, bool>();
        using var Host = await CreateHostAsync(Grants: Grants);
        const string Program = "Console.Write(\"{{literal source}}\");";
        var Source = JsonSerializer.Serialize(new { steps = new object[] {
            new { id = "code", action = "csharp", arguments = new { source = Program, input = "input {{run.id}}" } },
            new { id = "report", action = "save_report", arguments = new { title = "Output", content = "{{steps.code}}" } } } });
        await FluentActions.Awaiting(() => SaveAndQueueAsync(Host, Source)).Should().ThrowAsync<UnauthorizedAccessException>();
        Grants[AutomationPrograms.PermissionKey] = true;
        await using (var CheckScope = Host.Services.CreateAsyncScope())
        {
            var Recipes = CheckScope.ServiceProvider.GetRequiredService<AutomationRecipes>();
            await FluentActions.Awaiting(() => Recipes.ValidateToolsAsync(Recipes.Parse(Source), CheckScope.ServiceProvider,
                new("coach", AgentRoles.Coach, "owner"), CheckScope.ServiceProvider.GetRequiredService<ToolAccessService>(), default))
                .Should().ThrowAsync<UnauthorizedAccessException>();
        }
        var (Id, Run) = await SaveAndQueueAsync(Host, Source);
        await Host.StartAsync();
        try
        {
            var Capture = Host.Services.GetRequiredService<ProgramCapture>();
            await UntilAsync(() => Task.FromResult(Capture.Items.Count == 1));
            var Request = Capture.Items.Single();
            Request.Source.Should().Be(Program);
            Request.Input.Should().Be("input " + Run);
            Grants[AutomationPrograms.PermissionKey] = false;
            var Evidence = new AutomationProgramEvidence("hash", "sha256:image", 0, "Completed", "", 1);
            await (await Host.Services.GetRequiredService<IBus>().GetSendEndpoint(AutomationRegistration.SagaAddress))
                .Send(new AutomationStepCompleted(Run, 0, "private output", Program: Evidence));
            await WaitAsync(Host, Run, "Blocked");
            await using var Scope = Host.Services.CreateAsyncScope();
            var Detail = await Scope.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Reports.Should().BeEmpty();
            Detail.Steps[0].Output.Should().BeNull();
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public async Task ProgramDiagnosticsPersistWithFailedSaga()
    {
        using var Host = await CreateHostAsync(Grants: new() { [AutomationPrograms.PermissionKey] = true });
        var (Id, Run) = await SaveAndQueueAsync(Host, """{"steps":[{"id":"code","action":"csharp","arguments":{"source":"invalid code","input":""}}]}""");
        await Host.StartAsync();
        try
        {
            await UntilAsync(() => Task.FromResult(Host.Services.GetRequiredService<ProgramCapture>().Items.Count == 1));
            var Evidence = new AutomationProgramEvidence("hash", "sha256:image", 200, "BuildFailed", "error CS1002", 1);
            await (await Host.Services.GetRequiredService<IBus>().GetSendEndpoint(AutomationRegistration.SagaAddress))
                .Send(new AutomationStepFailed(Run, 0, "Build failed", Program: Evidence));
            await WaitAsync(Host, Run, "Failed");
            await using var Scope = Host.Services.CreateAsyncScope();
            var Detail = await Scope.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Steps[0].ProgramEvidence.Should().Contain("CS1002").And.Contain("sha256:image");
            (await Scope.ServiceProvider.GetRequiredService<AutomationService>().DetailAsync(Owner, Id, 0, default)).Automation.Status.Should().Be("Paused");
        }
        finally { await Host.StopAsync(); }
    }

    private async Task<IHost> CreateHostAsync(Mock<IScheduledActorPolicy>? Policy = null, FailReportCommit? Failure = null, bool Reset = true, Dictionary<string, bool>? Grants = null,
        bool JevConfigured = false, ToolChoiceResult? Decision = null)
    {
        var Host = new HostBuilder().ConfigureLogging(L => L.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(S =>
            {
                S.AddSingleton(TimeProvider.System);
                S.AddSingleton(Policy?.Object ?? PolicyMock().Object);
                S.AddSingleton(Mock.Of<ICoachAssignmentStore>());
                var Permissions = new Mock<IToolAccessStore>();
                Permissions.Setup(P => P.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Grants ?? new Dictionary<string, bool>());
                S.AddSingleton(Permissions.Object);
                S.AddSingleton<ITavilyMcpToolProvider>(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<AIFunction>()));
                S.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
                S.AddSingleton<ToolAccessService>(); S.AddSingleton<ScheduledJobAuthorization>(); S.AddSingleton<AutomationAuthorization>(); S.AddSingleton<AutomationRecipes>();
                S.AddScoped<AutomationService>(); S.AddScoped<AutomationSagaActions>();
                S.AddSingleton<SchedulingService>(); S.AddSingleton<AgentEventService>();
                S.AddSingleton<NotificationCapture>();
                S.AddSingleton<ProgramCapture>();
                S.AddSingleton(new IntegrationDatabase(Database.ConnectionString, Convert.ToBase64String(new byte[32])));
                S.AddSingleton<AutomationRuntimeStore>(); S.AddSingleton<AutomationSandboxStore>();
                S.AddScoped<AutomationOperationGateway>();
                var Snapshot = JevConfigured ? new JevRoutingSnapshot(new() { AllowUserContent = true }, "test-key") : JevRoutingSnapshot.Disabled;
                S.AddSingleton(Mock.Of<IJevRoutingSettings>(J => J.Current == Snapshot));
                var Decisions = new Mock<IToolDecisionClient>();
                Decisions.Setup(D => D.ChooseAsync(It.IsAny<ToolChoiceRequest>(), It.IsAny<JevRoutingSnapshot>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Decision ?? new ToolChoiceResult(null, Reason: "disabled"));
                S.AddSingleton(Decisions.Object);
                S.AddDbContext<AutomationDbContext>(O =>
                {
                    O.UseNpgsql(Database.ConnectionString, N => N.MigrationsHistoryTable("__EFMigrationsHistory", "automation"));
                    if (Failure is not null) O.AddInterceptors(Failure);
                });
                S.AddMassTransit(B => { B.AddAutomationMessaging(); B.AddConsumer<NotificationProbe>();
                    B.AddConsumer<ProgramProbe>().Endpoint(E => E.Name = AutomationPrograms.Queue);
                    B.UsingInMemory((C, B) => B.ConfigureEndpoints(C)); });
            }).Build();
        await using var Scope = Host.Services.CreateAsyncScope();
        var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        if (Reset) await Db.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS automation CASCADE");
        await Db.Database.MigrateAsync();
        await Host.Services.GetRequiredService<AutomationRuntimeStore>().InitializeAsync(default);
        return Host;
    }

    private static Mock<IScheduledActorPolicy> PolicyMock()
    {
        var Policy = new Mock<IScheduledActorPolicy>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync("Owner");
        Policy.Setup(P => P.ResolveRoleAsync("other", null, It.IsAny<CancellationToken>())).ReturnsAsync("Owner");
        return Policy;
    }
    private static async Task<(Guid, Guid)> SaveAndQueueAsync(IHost Host, string Source)
    {
        await using var Scope = Host.Services.CreateAsyncScope();
        var Service = Scope.ServiceProvider.GetRequiredService<AutomationService>();
        var Id = (await Service.SaveAsync(Owner, null, new("Test automation", Source), default)).Id;
        return (Id, (await Service.StartAsync(Id, null, default))!.Value);
    }
    private static Task WaitAsync(IHost Host, Guid Id, string Status) => UntilAsync(async () =>
    {
        await using var Scope = Host.Services.CreateAsyncScope();
        return await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Runs.AnyAsync(R => R.CorrelationId == Id && R.CurrentState == Status);
    });
    private static async Task UntilAsync(Func<Task<bool>> Check)
    {
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await Check()) await Task.Delay(50, Deadline.Token);
    }
    private sealed class FailReportCommit : SaveChangesInterceptor
    {
        public volatile bool Enabled;
        public int Attempts;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData EventData, InterceptionResult<int> Result, CancellationToken Token = default)
        {
            if (Enabled && EventData.Context!.ChangeTracker.Entries<AutomationReport>().Any(E => E.State == EntityState.Added))
            { Interlocked.Increment(ref Attempts); throw new InvalidOperationException("Synthetic transaction failure"); }
            return ValueTask.FromResult(Result);
        }
    }
    private sealed class NotificationCapture
    {
        public System.Collections.Concurrent.ConcurrentBag<DevicePushNotificationRequested> Items { get; } = [];
    }
    private sealed class ProgramCapture
    {
        public System.Collections.Concurrent.ConcurrentBag<ExecuteAutomationProgram> Items { get; } = [];
    }
    private sealed class ProgramProbe(ProgramCapture Capture) : IConsumer<ExecuteAutomationProgram>
    {
        public Task Consume(ConsumeContext<ExecuteAutomationProgram> Context) { Capture.Items.Add(Context.Message); return Task.CompletedTask; }
    }
    private sealed class NotificationProbe(NotificationCapture Capture) : IConsumer<DevicePushNotificationRequested>
    {
        public Task Consume(ConsumeContext<DevicePushNotificationRequested> Context) { Capture.Items.Add(Context.Message); return Task.CompletedTask; }
    }
}
