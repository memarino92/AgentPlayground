using System.Security.Cryptography;
using System.Text.Json;

using FluentAssertions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using PersonalAgent.Api.Automations;
using PersonalAgent.Api.Coding;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Api.Tests.Services;

public sealed partial class AutomationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    public async Task ParallelChildrenRestartReplayPreservesDependenciesAndIdentity(bool CodingNeedsResearch, bool RunningCoding, bool FailParent)
    {
        var Source = $$$"""
            {"parallelChildren":true,"steps":[
              {"id":"research","action":"public_research","arguments":{"questions":["What is in the public manual?","What is in the public changelog?"]}},
              {"id":"code","action":"coding_job",
               "arguments":{"instruction":"Implement the example."{{{(CodingNeedsResearch ? ",\"evidenceSteps\":[\"research\"]" : "")}}}}},
              {"id":"report","action":"save_report","arguments":{"title":"Mixed outcome","content":"{{steps.research}}\n{{steps.code}}"}}
            ]}
            """;
        var Grants = new Dictionary<string, bool>
        {
            [AutomationAuthorization.SuperviseKey] = true, [CodingJobs.Permission] = true,
            [AgentToolKeys.ResearchSwarm] = true, [AgentToolKeys.Tavily("tavily_search")] = true
        };
        var Publisher = new Mock<ICodingPublisher>(MockBehavior.Strict);
        Publisher.Setup(P => P.PrepareAsync(It.IsAny<CodingSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync(new string('a', 40));
        var FirstGate = new ResearchGate();
        var SecondGate = new ResearchGate();
        void Configure(IServiceCollection Services, ResearchGate Gate)
        {
            Services.AddSingleton<CodingJobStore>();
            Services.AddHttpClient<ChatGptOAuth>();
            Services.AddSingleton<ChatGptConnectionStore>();
            Services.AddSingleton<ChatGptModelCatalog>();
            Services.AddSingleton(Publisher.Object);
            Services.AddScoped<CodingJobService>();
            var Catalog = new Mock<IChatModelCatalog>();
            Catalog.Setup(C => C.FindModelAsync("deterministic", It.IsAny<CancellationToken>())).ReturnsAsync(new AvailableChatModel("deterministic", "Test", false));
            Services.AddSingleton(Catalog.Object);
            var Tavily = new Mock<ITavilyMcpToolProvider>();
            Tavily.SetupGet(T => T.IsAvailable).Returns(true);
            Tavily.Setup(T => T.GetTools()).Returns([Gate]);
            Services.AddSingleton(Tavily.Object);
            var Client = new Mock<IChatClient>();
            Client.Setup(C => C.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Deterministic public finding [1]")));
            var Factory = new Mock<IAgentChatClientFactory>();
            Factory.Setup(F => F.CreateForActor("deterministic", "owner")).Returns(Client.Object);
            Services.AddSingleton(Factory.Object);
            Services.AddSingleton<ResearchSwarm>();
        }
        var Schema = "children_" + Guid.NewGuid().ToString("N");
        using var First = await CreateHostAsync(Grants: Grants, Configure: S => Configure(S, FirstGate), SqlSchema: Schema);
        var Store = First.Services.GetRequiredService<CodingJobStore>();
        await Store.InitializeAsync(default);
        using var Key = RSA.Create(2048);
        var Revision = (await Store.SettingsAsync(default)).View.Revision;
        await Store.SaveSettingsAsync(new(Revision, new CodingSettings
        {
            Enabled = true, Checkpoint = "test-image", ImageId = "sha256:" + new string('b', 64), GitHubAppId = 1, InstallationId = 1
        }, "replace", "synthetic-key", "replace", Key.ExportRSAPrivateKeyPem()), default);
        var Actor = Owner with { SessionId = Guid.NewGuid().ToString("D"), ChatModelId = "deterministic" };
        var Id = Guid.NewGuid();
        Guid Run;
        await using (var Scope = First.Services.CreateAsyncScope())
            Run = (await Scope.ServiceProvider.GetRequiredService<AutomationService>().StartOneShotAsync(Actor, Id, "Mixed request", Source, default, Supervised: true)).RunId;
        var Request = AutomationStepConsumer.CodingRequestKey(Run, 1);
        CodingJob? Child = null;
        Guid? ResearchId = null;
        await First.StartAsync();
        try
        {
            await FirstGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (!CodingNeedsResearch)
                await UntilAsync(async () => (Child = await Store.FindRequestAsync(Owner.ActorId, Request, default)) is not null);
            await using var Scope = First.Services.CreateAsyncScope();
            var Steps = await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.Where(S => S.RunId == Run).OrderBy(S => S.Index).ToListAsync();
            Steps[0].Status.Should().Be("Running");
            Steps[0].Attempt.Should().Be(1);
            Steps[0].ChildId.Should().NotBeNull();
            ResearchId = Steps[0].ChildId;
            Steps[1].Status.Should().Be(CodingNeedsResearch ? "Pending" : "Running");
            Steps[2].Status.Should().Be("Pending");
            if (CodingNeedsResearch) (await Store.FindRequestAsync(Owner.ActorId, Request, default)).Should().BeNull();
            if (RunningCoding)
            {
                (await Store.ClaimAsync(Child!.Id, "synthetic-capability", "synthetic-railway", "test", default)).Should().BeTrue();
                (await Store.ReserveModelRequestAsync(Child.Id, "synthetic-capability", 12, default)).Should().BeTrue();
                Child = await Store.FindRequestAsync(Owner.ActorId, Request, default);
                Child!.Status.Should().Be("Running");
            }
        }
        finally { await First.StopAsync(); }

        // The old process has stopped during provider I/O. Simulate elapsed lease time, not a
        // completed research call; the durable SQL queue and step rows are the only handoff.
        await using (var Scope = First.Services.CreateAsyncScope())
            await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.Where(S => S.RunId == Run && S.Index == 0)
                .ExecuteUpdateAsync(U => U.SetProperty(S => S.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        using var Restarted = await CreateHostAsync(Reset: false, Grants: Grants, Configure: S => Configure(S, SecondGate), SqlSchema: Schema);
        await Restarted.StartAsync();
        try
        {
            var Scheduler = new AutomationScheduler(Restarted.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AutomationScheduler>.Instance);
            await Scheduler.SweepAsync(default);
            await SecondGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var Endpoint = await Restarted.Services.GetRequiredService<IBus>().GetSendEndpoint(AutomationRegistration.SagaAddress);
            if (FailParent)
            {
                await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepFailed(Run, 0, "Research failed", Attempt: 2));
                await WaitAsync(Restarted, Run, "Failed");
                await UntilAsync(async () => (await Store.FindRequestAsync(Owner.ActorId, Request, default))!.Status == "Cancelled");
                (await Store.FindRequestAsync(Owner.ActorId, Request, default))!.Status.Should().Be("Cancelled");
                SecondGate.Release.TrySetResult();
                await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 1, "late coding", Attempt: 1));
                await using var Failed = Restarted.Services.CreateAsyncScope();
                var FailedDetail = await Failed.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
                FailedDetail.Reports.Should().BeEmpty();
                FailedDetail.Steps[1].Status.Should().Be("Blocked");
                FailedDetail.Steps[1].Output.Should().BeNull();
                return;
            }
            // A completion from the abandoned generation cannot unblock dependencies.
            await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 0, "stale research", Attempt: 1));
            await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepFailed(Run, 0, "stale failure", Attempt: 1));
            if (!CodingNeedsResearch)
            {
                const string Draft = "https://github.com/example/project/pull/42";
                var Recovered = (await Store.FindRequestAsync(Owner.ActorId, Request, default))!;
                Recovered.Should().BeEquivalentTo(Child);
                (await Store.SaveAsync(Child! with { Status = "PrOpened", PullRequestUrl = Draft }, Child!.Status, default)).Should().BeTrue();
                await Scheduler.SweepAsync(default);
                await UntilAsync(async () =>
                {
                    await using var Scope = Restarted.Services.CreateAsyncScope();
                    return await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.AnyAsync(S => S.RunId == Run && S.Index == 1 && S.Status == "Completed");
                });
                await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 1, "duplicate coding", Attempt: 1));
            }
            await using (var Scope = Restarted.Services.CreateAsyncScope())
            {
                var Steps = await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.Where(S => S.RunId == Run).OrderBy(S => S.Index).ToListAsync();
                Steps[0].Status.Should().Be("Running");
                Steps[0].Attempt.Should().Be(2);
                Steps[1].Attempt.Should().Be(CodingNeedsResearch ? 0 : 1);
                Steps[2].Status.Should().Be("Pending");
                (await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Reports.CountAsync()).Should().Be(0);
            }
            SecondGate.Release.TrySetResult();
            if (CodingNeedsResearch)
            {
                await UntilAsync(async () => (Child = await Store.FindRequestAsync(Owner.ActorId, Request, default)) is not null);
                Child!.FollowUpEvidence.Should().Contain("Deterministic public finding").And.Contain("https://example.org/manual");
                (await Store.CancelAsync(Child.Id, default)).Should().BeTrue(); // Existing cancellation is terminal evidence, not a fresh job.
                await Scheduler.SweepAsync(default);
            }
            await WaitAsync(Restarted, Run, "Completed");
            await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 0, "duplicate research", Attempt: 2));
            await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 1, "duplicate coding", Attempt: 1));
            await SendAndObserveAsync(Restarted, AutomationRegistration.StepAddress, new ExecuteAutomationStep(Run, 0, 2));
            await Scheduler.SweepAsync(default);
            await using var Check = Restarted.Services.CreateAsyncScope();
            var Detail = await Check.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Steps.Should().OnlyContain(S => S.Status == "Completed");
            Detail.Steps[0].Output.Should().NotContain("stale").And.NotContain("duplicate");
            Detail.Steps[0].Attempt.Should().Be(2);
            Detail.Steps[0].ChildId.Should().Be(ResearchId);
            Detail.Steps[1].Attempt.Should().Be(1);
            Detail.Steps[1].Output.Should().Contain(Child!.Id.ToString()).And.NotContain("duplicate");
            Detail.Reports.Should().ContainSingle().Which.Content.Should().Be(Detail.Steps[0].Output + "\n" + Detail.Steps[1].Output);
            var FinalStore = Restarted.Services.GetRequiredService<CodingJobStore>();
            var FinalChild = (await FinalStore.FindRequestAsync(Owner.ActorId, Request, default))!;
            FinalChild.Id.Should().Be(Child.Id);
            FinalChild.ModelRequests.Should().Be(Child.ModelRequests);
            FinalChild.Attempt.Should().Be(Child.Attempt);
            FinalChild.AttemptDeadline.Should().Be(Child.AttemptDeadline);
            FinalChild.Deadline.Should().Be(Child.Deadline);
            FinalChild.Settings.Should().BeEquivalentTo(Child.Settings);
            if (CodingNeedsResearch)
            {
                Detail.Steps[1].Output.Should().Contain("Cancelled");
                (await Store.FindRequestAsync(Owner.ActorId, Request, default))!.Status.Should().Be("Cancelled");
            }
            Publisher.Verify(P => P.PrepareAsync(It.IsAny<CodingSettings>(), It.IsAny<CancellationToken>()), Times.Once);
            SecondGate.Calls.Should().Be(2); // Two public questions; no terminal or queued coding work was re-run.
        }
        finally { await Restarted.StopAsync(); }
    }

    private static async Task SendAndObserveAsync<T>(Microsoft.Extensions.Hosting.IHost Host, Uri Address, T Message) where T : class
    {
        var Id = Guid.NewGuid();
        await (await Host.Services.GetRequiredService<IBus>().GetSendEndpoint(Address)).Send(Message, C => C.MessageId = Id);
        await UntilAsync(async () =>
        {
            await using var Scope = Host.Services.CreateAsyncScope();
            return await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Set<InboxState>().AnyAsync(I => I.MessageId == Id && I.Delivered != null);
        });
    }

    [Fact]
    public async Task CommittedResearchCompletionReplaysWithoutCallingProviderAfterRestart()
    {
        var Grants = new Dictionary<string, bool> { [AutomationAuthorization.SuperviseKey] = true,
            [AgentToolKeys.ResearchSwarm] = true, [AgentToolKeys.Tavily("tavily_search")] = true };
        var Gate = new ResearchGate();
        Gate.Release.TrySetResult();
        void Configure(IServiceCollection Services)
        {
            var Tavily = new Mock<ITavilyMcpToolProvider>();
            Tavily.SetupGet(T => T.IsAvailable).Returns(true);
            Tavily.Setup(T => T.GetTools()).Returns([Gate]);
            Services.AddSingleton(Tavily.Object);
            var Catalog = new Mock<IChatModelCatalog>();
            Catalog.Setup(C => C.FindModelAsync("deterministic", It.IsAny<CancellationToken>())).ReturnsAsync(new AvailableChatModel("deterministic", "Test", false));
            Services.AddSingleton(Catalog.Object);
            var Client = new Mock<IChatClient>();
            Client.Setup(C => C.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Committed finding [1]")));
            var Factory = new Mock<IAgentChatClientFactory>();
            Factory.Setup(F => F.CreateForActor("deterministic", "owner")).Returns(Client.Object);
            Services.AddSingleton(Factory.Object);
            Services.AddSingleton<ResearchSwarm>();
        }
        var Barrier = new HoldResearchIntegration();
        var Schema = "completion_" + Guid.NewGuid().ToString("N");
        using var First = await CreateHostAsync(Grants: Grants, Configure: Configure, SqlSchema: Schema, Interceptor: Barrier);
        Guid Run;
        var Id = Guid.NewGuid();
        await using (var Scope = First.Services.CreateAsyncScope())
            Run = (await Scope.ServiceProvider.GetRequiredService<AutomationService>().StartOneShotAsync(
                Owner with { SessionId = Guid.NewGuid().ToString(), ChatModelId = "deterministic" }, Id, "Result replay", """
                {"parallelChildren":true,"steps":[
                  {"id":"research","action":"public_research","arguments":{"questions":["Public manual?","Public changelog?"]}},
                  {"id":"report","action":"save_report","arguments":{"title":"Result","content":"{{steps.research}}"}}]}
                """, default, Supervised: true)).RunId;
        await First.StartAsync();
        try
        {
            // Reaching saga integration means the research consumer has committed and delivered
            // its completion outbox. Interrupt before that completion can commit to the parent.
            await Barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Gate.Calls.Should().Be(2);
        }
        finally { await First.StopAsync(); }
        using var Restarted = await CreateHostAsync(Reset: false, Grants: Grants, Configure: Configure, SqlSchema: Schema);
        await Restarted.StartAsync();
        try
        {
            await WaitAsync(Restarted, Run, "Completed");
            await SendAndObserveAsync(Restarted, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 0, "duplicate", Attempt: 1));
            await using var Scope = Restarted.Services.CreateAsyncScope();
            var Detail = await Scope.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Steps[0].Attempt.Should().Be(1);
            Detail.Steps[0].Output.Should().Contain("Committed finding").And.NotContain("duplicate");
            Detail.Reports.Should().ContainSingle().Which.Content.Should().Be(Detail.Steps[0].Output);
            Gate.Calls.Should().Be(2);
        }
        finally { await Restarted.StopAsync(); }
    }

    private sealed class HoldResearchIntegration : SaveChangesInterceptor
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData EventData, InterceptionResult<int> Result, CancellationToken Token = default)
        {
            if (EventData.Context!.ChangeTracker.Entries<AutomationStepExecution>().Any(E => E.Entity.Action == "public_research" && E.Entity.Status == "Completed"))
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, Token);
            }
            return Result;
        }
    }

    [Fact]
    public async Task CancellationBeforeCodingCreationSurvivesRestartAndPreventsClaim()
    {
        using var First = await CreateHostAsync(Configure: S => S.AddSingleton<CodingJobStore>());
        var Store = First.Services.GetRequiredService<CodingJobStore>();
        await Store.InitializeAsync(default);
        var Run = await SeedParallelRunAsync(First, Guid.NewGuid(), "text", "Queued", DateTimeOffset.UtcNow.AddMinutes(5));
        await using (var Scope = First.Services.CreateAsyncScope())
        {
            var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            await Db.Steps.Where(S => S.RunId == Run).ExecuteUpdateAsync(U => U.SetProperty(S => S.Action, "coding_job").SetProperty(S => S.Status, "Blocked"));
            await Db.Runs.Where(R => R.CorrelationId == Run).ExecuteUpdateAsync(U => U.SetProperty(R => R.CurrentState, "Failed")
                .SetProperty(R => R.Error, "Sibling failed").SetProperty(R => R.FinishedAt, DateTimeOffset.UtcNow));
        }
        await First.StartAsync();
        try
        {
            // Cancellation is acknowledged before a job exists, as when repository preparation
            // is still in flight. The only handoff to the next host is committed database state.
            await SendAndObserveAsync(First, AutomationRegistration.StepAddress, new CancelAutomationChild(Run, 0));
        }
        finally { await First.StopAsync(); }
        using var Restarted = await CreateHostAsync(Reset: false, Configure: S => S.AddSingleton<CodingJobStore>());
        var Recovered = Restarted.Services.GetRequiredService<CodingJobStore>();
        await Recovered.InitializeAsync(default);
        var Request = AutomationStepConsumer.CodingRequestKey(Run, 0);
        var Job = await Recovered.CreateAsync(new() { Id = Guid.NewGuid(), ActorId = Owner.ActorId, Subject = Owner.SubjectProfileId,
            Instruction = "Prepared before failure", CreatedAt = DateTimeOffset.UtcNow, Deadline = DateTimeOffset.UtcNow.AddMinutes(20) }, Request, default);
        Job.Status.Should().Be("Cancelled");
        (await Recovered.ClaimAsync(Job.Id, "capability", "railway", "test", default)).Should().BeFalse();
        (await Recovered.ReserveModelRequestAsync(Job.Id, "capability", 12, default)).Should().BeFalse();
        (await Recovered.CreateAsync(Job with { Id = Guid.NewGuid() }, Request, default)).Id.Should().Be(Job.Id);
        Job.ModelRequests.Should().Be(0);
    }

    [Theory]
    [InlineData(AutomationAuthorization.SuperviseKey)]
    [InlineData(AgentToolKeys.ResearchSwarm)]
    [InlineData("Tavily.tavily_search")]
    [InlineData("owner")]
    [InlineData("model")]
    public async Task CompletionRevalidatesCurrentGrantsBeforeIntegrating(string Revoked)
    {
        var Policy = PolicyMock();
        var Grants = new Dictionary<string, bool> { [AutomationAuthorization.SuperviseKey] = true,
            [AgentToolKeys.ResearchSwarm] = true, [AgentToolKeys.Tavily("tavily_search")] = true };
        var Catalog = new Mock<IChatModelCatalog>();
        Catalog.Setup(C => C.FindModelAsync("deterministic", It.IsAny<CancellationToken>())).ReturnsAsync(new AvailableChatModel("deterministic", "Test", false));
        using var Host = await CreateHostAsync(Policy: Policy, Grants: Grants, Configure: S =>
        {
            var Tavily = new Mock<ITavilyMcpToolProvider>();
            Tavily.SetupGet(T => T.IsAvailable).Returns(true);
            Tavily.Setup(T => T.GetTools()).Returns([new ResearchGate()]);
            S.AddSingleton(Tavily.Object);
            S.AddSingleton(Catalog.Object);
        });
        var Run = await SeedParallelRunAsync(Host, Guid.NewGuid(), "public_research", "Running", DateTimeOffset.UtcNow.AddMinutes(5));
        if (Revoked == "owner") Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        else if (Revoked == "model") Catalog.Setup(C => C.FindModelAsync("deterministic", It.IsAny<CancellationToken>())).ReturnsAsync((AvailableChatModel?)null);
        else Grants[Revoked == "Tavily.tavily_search" ? AgentToolKeys.Tavily("tavily_search") : Revoked] = false;
        await Host.StartAsync();
        try
        {
            // Do not reconcile first: completion integration itself must reject the new revocation.
            await SendAndObserveAsync(Host, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 0, "untrusted finding", Attempt: 1));
            await WaitAsync(Host, Run, "Blocked");
            await using var Scope = Host.Services.CreateAsyncScope();
            var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            (await Db.Steps.SingleAsync(S => S.RunId == Run)).Output.Should().BeNull();
            (await Db.Reports.CountAsync()).Should().Be(0);
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public async Task ReconciliationCannotOverwriteConcurrentRenewedClaim()
    {
        using var Host = await CreateHostAsync(Grants: new() { [AutomationAuthorization.SuperviseKey] = true });
        // A text barrier needs no providers; use a live lease to isolate the persistence race.
        var Run = await SeedParallelRunAsync(Host, Guid.NewGuid(), "text", "Queued", DateTimeOffset.UtcNow.AddMinutes(5));
        await using var ClaimScope = Host.Services.CreateAsyncScope();
        var ClaimDb = ClaimScope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        await ClaimDb.Steps.Where(S => S.RunId == Run).ExecuteUpdateAsync(U => U.SetProperty(S => S.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await using var ClaimTransaction = await ClaimDb.Database.BeginTransactionAsync();
        await ClaimDb.Steps.Where(S => S.RunId == Run).ExecuteUpdateAsync(U => U.SetProperty(S => S.Status, "Running")
            .SetProperty(S => S.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(75)));
        await using var RecoveryScope = Host.Services.CreateAsyncScope();
        var RecoveryDb = RecoveryScope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        await using var RecoveryTransaction = await RecoveryDb.Database.BeginTransactionAsync();
        var Context = new Mock<ConsumeContext>();
        Context.Setup(C => C.GetSendEndpoint(It.IsAny<Uri>())).ReturnsAsync(Mock.Of<ISendEndpoint>());
        var Parent = await RecoveryDb.Runs.SingleAsync(R => R.CorrelationId == Run);
        var Recover = RecoveryScope.ServiceProvider.GetRequiredService<AutomationSagaActions>().ReconcileAsync(Parent, Context.Object);
        try
        {
            await UntilAsync(async () =>
            {
                await using var Connection = new Npgsql.NpgsqlConnection(Database.ConnectionString);
                await Connection.OpenAsync();
                await using var Query = new Npgsql.NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE 'SELECT * FROM automation.%Steps%FOR UPDATE%')", Connection);
                return (bool)(await Query.ExecuteScalarAsync())!;
            });
        }
        finally { await ClaimTransaction.CommitAsync(); }
        await Recover;
        await RecoveryDb.SaveChangesAsync();
        await RecoveryTransaction.CommitAsync();
        var Step = await RecoveryDb.Steps.SingleAsync(S => S.RunId == Run);
        Step.Status.Should().Be("Running");
        Step.Attempt.Should().Be(1);
        Step.LeaseUntil.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(50));
    }

    [Theory]
    [InlineData("Queued", 1, false, false, 2, "Queued")]
    [InlineData("Running", 1, true, false, 1, "Running")]
    [InlineData("Running", 3, false, false, 3, "Blocked")]
    [InlineData("Queued", 1, false, true, 1, "Blocked")]
    public async Task ReconciliationDispatchesOnlyEligibleChildren(string Status, int Attempt, bool LiveLease, bool ExpiredDeadline, int ExpectedAttempt, string ExpectedStatus)
    {
        var Grants = new Dictionary<string, bool> { [AutomationAuthorization.SuperviseKey] = true,
            [AgentToolKeys.ResearchSwarm] = true, [AgentToolKeys.Tavily("tavily_search")] = true };
        using var Host = await CreateHostAsync(Grants: Grants, Configure: S =>
        {
            var Tavily = new Mock<ITavilyMcpToolProvider>();
            Tavily.SetupGet(T => T.IsAvailable).Returns(true);
            Tavily.Setup(T => T.GetTools()).Returns([new ResearchGate()]);
            S.AddSingleton(Tavily.Object);
            var Catalog = new Mock<IChatModelCatalog>();
            Catalog.Setup(C => C.FindModelAsync("deterministic", It.IsAny<CancellationToken>())).ReturnsAsync(new AvailableChatModel("deterministic", "Test", false));
            S.AddSingleton(Catalog.Object);
        });
        var Run = await SeedParallelRunAsync(Host, Guid.NewGuid(), "public_research", Status,
            DateTimeOffset.UtcNow.AddMinutes(ExpiredDeadline ? -1 : 5));
        await using var Scope = Host.Services.CreateAsyncScope();
        var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        await Db.Steps.Where(S => S.RunId == Run).ExecuteUpdateAsync(U => U.SetProperty(S => S.Attempt, Attempt)
            .SetProperty(S => S.LeaseUntil, DateTimeOffset.UtcNow.AddMinutes(LiveLease ? 1 : -1)));
        // Invoke under the same transaction boundary as the saga, without running a step consumer.
        await using var Transaction = await Db.Database.BeginTransactionAsync();
        var Context = new Mock<ConsumeContext>();
        var Endpoint = new Mock<ISendEndpoint>();
        Context.Setup(C => C.GetSendEndpoint(It.IsAny<Uri>())).ReturnsAsync(Endpoint.Object);
        var Parent = await Db.Runs.SingleAsync(R => R.CorrelationId == Run);
        await Scope.ServiceProvider.GetRequiredService<AutomationSagaActions>().ReconcileAsync(Parent, Context.Object);
        await Db.SaveChangesAsync();
        await Transaction.CommitAsync();
        var Step = await Db.Steps.SingleAsync(S => S.RunId == Run);
        Step.Attempt.Should().Be(ExpectedAttempt);
        Step.Status.Should().Be(ExpectedStatus);
        Step.Output.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredQueuedChildCannotCallProviders()
    {
        using var Host = await CreateHostAsync();
        var Run = await SeedParallelRunAsync(Host, Guid.NewGuid(), "public_research", "Queued", DateTimeOffset.UtcNow.AddSeconds(-1));
        await Host.StartAsync();
        try
        {
            // No research/provider services are registered: any I/O would fail this step.
            await SendAndObserveAsync(Host, AutomationRegistration.StepAddress, new ExecuteAutomationStep(Run, 0, 1));
            await using var Scope = Host.Services.CreateAsyncScope();
            var Step = await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.SingleAsync(S => S.RunId == Run);
            Step.Status.Should().Be("Queued");
            Step.Attempt.Should().Be(1);
            Step.Output.Should().BeNull();
        }
        finally { await Host.StopAsync(); }
    }

    [Fact]
    public async Task DelayedDeliveryRenewsLeaseAndRevocationBlocksActiveAttempt()
    {
        var Gate = new ResearchGate();
        var Grants = new Dictionary<string, bool> { [AutomationAuthorization.SuperviseKey] = true,
            [AgentToolKeys.ResearchSwarm] = true, [AgentToolKeys.Tavily("tavily_search")] = true };
        using var Host = await CreateHostAsync(Grants: Grants, Configure: S =>
        {
            var Tavily = new Mock<ITavilyMcpToolProvider>();
            Tavily.SetupGet(T => T.IsAvailable).Returns(true);
            Tavily.Setup(T => T.GetTools()).Returns([Gate]);
            S.AddSingleton(Tavily.Object);
            var Catalog = new Mock<IChatModelCatalog>();
            Catalog.Setup(C => C.FindModelAsync("deterministic", It.IsAny<CancellationToken>())).ReturnsAsync(new AvailableChatModel("deterministic", "Test", false));
            S.AddSingleton(Catalog.Object);
            S.AddSingleton(Mock.Of<IAgentChatClientFactory>());
            S.AddSingleton<ResearchSwarm>();
        });
        var Run = await SeedParallelRunAsync(Host, Guid.NewGuid(), "public_research", "Queued", DateTimeOffset.UtcNow.AddMinutes(5));
        await using (var Scope = Host.Services.CreateAsyncScope())
            await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.Where(S => S.RunId == Run)
                .ExecuteUpdateAsync(U => U.SetProperty(S => S.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(1)));
        await Host.StartAsync();
        try
        {
            await (await Host.Services.GetRequiredService<IBus>().GetSendEndpoint(AutomationRegistration.StepAddress)).Send(new ExecuteAutomationStep(Run, 0, 1));
            await Gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await SendAndObserveAsync(Host, AutomationRegistration.SagaAddress, new ReconcileAutomation(Run));
            await using (var Scope = Host.Services.CreateAsyncScope())
            {
                var Step = await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.SingleAsync(S => S.RunId == Run);
                Step.LeaseUntil.Should().BeAfter(DateTimeOffset.UtcNow.AddSeconds(50));
                Step.Attempt.Should().Be(1);
                Step.Status.Should().Be("Running");
            }
            Grants[AgentToolKeys.ResearchSwarm] = false;
            await SendAndObserveAsync(Host, AutomationRegistration.SagaAddress, new ReconcileAutomation(Run));
            await WaitAsync(Host, Run, "Blocked");
            await SendAndObserveAsync(Host, AutomationRegistration.SagaAddress, new AutomationStepCompleted(Run, 0, "revoked evidence", Attempt: 1));
            await using var Check = Host.Services.CreateAsyncScope();
            (await Check.ServiceProvider.GetRequiredService<AutomationDbContext>().Steps.SingleAsync(S => S.RunId == Run)).Output.Should().BeNull();
            Gate.Calls.Should().Be(2);
        }
        finally { Gate.Release.TrySetResult(); await Host.StopAsync(); }
    }

    [Fact]
    public async Task DueSchedulingDoesNotStarveBehindFiftyActiveDefinitions()
    {
        using var Host = await CreateHostAsync();
        await using var Scope = Host.Services.CreateAsyncScope();
        var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        var Now = DateTimeOffset.UtcNow;
        var Eligible = Guid.NewGuid();
        for (var Index = 0; Index <= 50; Index++)
        {
            var Id = Index == 50 ? Eligible : Guid.NewGuid();
            Db.Automations.Add(new() { Id = Id, Name = "Due fixture", ActorId = Owner.ActorId, SubjectProfileId = Owner.SubjectProfileId,
                Version = 1, Status = "Active", NextRunAt = Now.AddMinutes(Index - 60) });
            Db.Versions.Add(new() { AutomationId = Id, Version = 1, Source = Source });
            if (Index < 50) Db.Runs.Add(new() { CorrelationId = Guid.NewGuid(), AutomationId = Id, Version = 1, CurrentState = "Running", StartedAt = Now });
        }
        await Db.SaveChangesAsync();
        var Scheduler = new AutomationScheduler(Host.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AutomationScheduler>.Instance);
        await Scheduler.SweepAsync(default);
        (await Db.Runs.CountAsync(R => R.AutomationId == Eligible)).Should().Be(1);
        (await Db.Runs.CountAsync()).Should().Be(51);
    }

    [Fact]
    public async Task SerialWatchdogVisitsBeyondSkippedParallelCandidates()
    {
        using var Host = await CreateHostAsync();
        var Last = Guid.Empty;
        for (var Index = 1; Index <= 51; Index++)
        {
            Last = await SeedParallelRunAsync(Host, Guid.Parse($"00000000-0000-0000-0000-{Index:D12}"), "text", "Queued", DateTimeOffset.UtcNow.AddMinutes(5));
            await using var Scope = Host.Services.CreateAsyncScope();
            var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
            await Db.Steps.Where(S => S.RunId == Last).ExecuteUpdateAsync(U => U.SetProperty(S => S.ChildId, (Guid?)null));
            await Db.Runs.Where(R => R.CorrelationId == Last).ExecuteUpdateAsync(U => U.SetProperty(R => R.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-10)));
            if (Index == 51)
            {
                var Parent = await Db.Runs.SingleAsync(R => R.CorrelationId == Last);
                await Db.Versions.Where(V => V.AutomationId == Parent.AutomationId).ExecuteUpdateAsync(U => U.SetProperty(V => V.Source, Source));
            }
        }
        var Scheduler = new AutomationScheduler(Host.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AutomationScheduler>.Instance);
        await Scheduler.SweepAsync(default);
        await Scheduler.SweepAsync(default);
        await using var Check = Host.Services.CreateAsyncScope();
        var Messages = await Check.ServiceProvider.GetRequiredService<AutomationDbContext>().Set<OutboxMessage>()
            .Where(M => M.MessageType.Contains(nameof(AutomationStepFailed))).ToListAsync();
        Messages.Should().ContainSingle().Which.Body.Should().Contain(Last.ToString());
    }

    [Fact]
    public async Task ReconciliationVisitsBeyondFirstFiftyParents()
    {
        using var Host = await CreateHostAsync(Grants: new() { [AutomationAuthorization.SuperviseKey] = true });
        var Runs = new List<Guid>();
        for (var Index = 1; Index <= 51; Index++)
            Runs.Add(await SeedParallelRunAsync(Host, Guid.Parse($"00000000-0000-0000-0000-{Index:D12}"), "text", "Queued", DateTimeOffset.UtcNow.AddSeconds(-1)));
        await Host.StartAsync();
        try
        {
            var Scheduler = new AutomationScheduler(Host.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<AutomationScheduler>.Instance);
            await Scheduler.SweepAsync(default);
            await Scheduler.SweepAsync(default);
            await WaitAsync(Host, Runs[^1], "Blocked");
            await UntilAsync(async () =>
            {
                await using var Scope = Host.Services.CreateAsyncScope();
                return await Scope.ServiceProvider.GetRequiredService<AutomationDbContext>().Runs.CountAsync(R => R.CurrentState == "Blocked") == 51;
            });
        }
        finally { await Host.StopAsync(); }
    }

    [Theory]
    [InlineData("csharp", 3)]
    [InlineData("dockerfile", 15)]
    public async Task ParallelProgramBarrierKeepsExistingWatchdogAndFailureEvidence(string Action, int MinutesLeft)
    {
        var Grants = new Dictionary<string, bool> { [AutomationAuthorization.SuperviseKey] = true,
            [AutomationPrograms.PermissionKey] = true, [AutomationPrograms.RunSandboxPermissionKey] = true };
        using var Host = await CreateHostAsync(Grants: Grants);
        var Id = Guid.NewGuid();
        var Source = JsonSerializer.Serialize(new { parallelChildren = true, steps = new[] {
            new { id = "program", action = Action, arguments = new { source = "literal source", input = "" } } } });
        Guid Run;
        await using (var Scope = Host.Services.CreateAsyncScope())
            Run = (await Scope.ServiceProvider.GetRequiredService<AutomationService>().StartOneShotAsync(Owner with { SessionId = Guid.NewGuid().ToString() }, Id, "Program barrier", Source, default, Supervised: true)).RunId;
        await Host.StartAsync();
        try
        {
            await UntilAsync(() => Task.FromResult(Host.Services.GetRequiredService<ProgramCapture>().Items.Count == 1));
            await using (var Scope = Host.Services.CreateAsyncScope())
            {
                var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
                var Step = await Db.Steps.SingleAsync(S => S.RunId == Run);
                var Remaining = (Step.LeaseUntil - DateTimeOffset.UtcNow)!.Value.TotalMinutes;
                Remaining.Should().BeGreaterThan(MinutesLeft);
                // Simulate a program older than 75 seconds but inside its original watchdog.
                Step.LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(MinutesLeft);
                await Db.SaveChangesAsync();
            }
            await SendAndObserveAsync(Host, AutomationRegistration.SagaAddress, new ReconcileAutomation(Run));
            await WaitAsync(Host, Run, "Running");
            var Evidence = new AutomationProgramEvidence("hash", "sha256:image", 200, "BuildFailed", "diagnostic", 1);
            await SendAndObserveAsync(Host, AutomationRegistration.SagaAddress, new AutomationStepFailed(Run, 0, "Build failed", Program: Evidence));
            await WaitAsync(Host, Run, "Failed");
            await using var Check = Host.Services.CreateAsyncScope();
            var Detail = await Check.ServiceProvider.GetRequiredService<AutomationService>().RunDetailAsync(Owner, Id, Run, default);
            Detail.Steps[0].ProgramEvidence.Should().Contain("diagnostic");
        }
        finally { await Host.StopAsync(); }
    }

    private static async Task<Guid> SeedParallelRunAsync(Microsoft.Extensions.Hosting.IHost Host, Guid RunId, string Action, string Status, DateTimeOffset Deadline)
    {
        await using var Scope = Host.Services.CreateAsyncScope();
        var Db = Scope.ServiceProvider.GetRequiredService<AutomationDbContext>();
        var Id = Guid.NewGuid();
        var Arguments = Action == "public_research" ? "{\"questions\":[\"Public manual?\",\"Public changelog?\"]}" : "{\"text\":\"literal\"}";
        Db.Automations.Add(new() { Id = Id, Name = "Recovery fixture", ActorId = Owner.ActorId, SubjectProfileId = Owner.SubjectProfileId, Version = 1 });
        Db.Versions.Add(new() { AutomationId = Id, Version = 1, Source = "{\"supervised\":true,\"supervisedModelId\":\"deterministic\",\"parallelChildren\":true,\"steps\":[{\"id\":\"child\",\"action\":\"" + Action + "\",\"arguments\":" + Arguments + "}]}" });
        Db.Runs.Add(new() { CorrelationId = RunId, AutomationId = Id, Version = 1, CurrentState = "Running", StartedAt = DateTimeOffset.UtcNow });
        Db.Steps.Add(new() { RunId = RunId, Index = 0, StepId = "child", Action = Action, ChildId = Guid.NewGuid(), Attempt = 1,
            Status = Status, Deadline = Deadline, LeaseUntil = Deadline });
        await Db.SaveChangesAsync();
        return RunId;
    }

    private sealed class ResearchGate : AIFunction
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public override string Name => "tavily_search";
        public override JsonElement JsonSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments Arguments, CancellationToken CancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 2) Entered.TrySetResult();
            await Release.Task.WaitAsync(CancellationToken);
            return new { results = new[] { new { url = "https://example.org/manual", title = "Manual", content = "Deterministic source" } } };
        }
    }
}
