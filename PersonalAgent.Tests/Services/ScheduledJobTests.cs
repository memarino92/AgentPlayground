using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Commands;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class ScheduledJobTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    internal async Task<(ScheduledJobStore Store, AgentMemoryOptions Options)> SetupAsync()
    {
        var Options = new AgentMemoryOptions { ConnectionString = Database.ConnectionString, Schema = "jobs_" + Guid.NewGuid().ToString("N") };
        var Store = new ScheduledJobStore(Microsoft.Extensions.Options.Options.Create(Options));
        await using var Connection = await Store.OpenAsync(default);
        await using var Command = new NpgsqlCommand($"CREATE SCHEMA {Options.Schema};" + ScheduledJobStore.SchemaSql(Options.Schema) + CoachCallOutbox.SchemaSql(Options.Schema), Connection);
        await Command.ExecuteNonQueryAsync();
        return (Store, Options);
    }

    internal static ScheduledJob Job() => new(Guid.NewGuid(), "owner", null, "owner", "Synthetic instruction", DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddSeconds(-1), "Scheduled", null, null, null, true, Guid.NewGuid(), 0, DateTimeOffset.UtcNow);

    private static ExecuteAgentTask Delivery(ScheduledJob Job) => new()
    {
        TaskId = Job.TaskId, TenantId = "forged", UserId = "other", Instruction = "Replace the stored instruction",
        CorrelationId = Job.CorrelationId, ExecuteAtUtc = DateTimeOffset.UtcNow
    };

    private static ScheduledJobExecutionService Execution(ScheduledJobStore Store, FakeRunner Runner, Mock<IScheduledActorPolicy>? Policy = null)
    {
        Policy ??= new Mock<IScheduledActorPolicy>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync("Owner");
        var Permissions = new Mock<IToolAccessStore>();
        Permissions.Setup(P => P.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, bool>());
        var Registry = new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<Microsoft.Extensions.AI.AIFunction>()));
        var Auth = new ScheduledJobAuthorization(Policy.Object, Mock.Of<ICoachAssignmentStore>(), new ToolAccessService(Permissions.Object, Registry));
        return new(Store, Auth, Runner, NullLogger<ScheduledJobExecutionService>.Instance);
    }

    [Fact]
    public async Task DuplicateAndRestartReturnSameResultAndSingleNotification()
    {
        var (Store, Options) = await SetupAsync();
        var Original = Job();
        await Store.CreateAsync(Original, default);
        var Runner = new FakeRunner();
        var First = await Execution(Store, Runner).ExecuteAsync(Delivery(Original), default);
        First.Status.Should().Be("Completed");
        var Second = await Execution(new(Microsoft.Extensions.Options.Options.Create(Options)), Runner).ExecuteAsync(Delivery(Original), default);
        Second.Should().Be(First);
        Runner.Runs.Should().Be(1);
        Runner.Instruction.Should().Be(Original.Instruction);
        Runner.Access!.ActorId.Should().Be("owner");
        (await Store.GetAsync(Original.TaskId, default))!.SessionId.Should().Be(Original.TaskId.ToString());
        (await Store.GetAttemptsAsync(Original.TaskId, default)).Should().ContainSingle().Which.Status.Should().Be("Completed");
        await using var Connection = await Store.OpenAsync(default);
        await using var Count = new NpgsqlCommand($"SELECT count(*) FROM {Options.Schema}.coach_call_outbox WHERE message_type = 'NotificationRequested'", Connection);
        (await Count.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Theory]
    [InlineData(null, "NeedsReview")]
    [InlineData("Durable result", "Completed")]
    public async Task InterruptedRunRecoversSavedResponseOrRequiresReview(string? Saved, string Expected)
    {
        var (Store, _) = await SetupAsync();
        var Original = Job();
        await Store.CreateAsync(Original with { Status = "Running", SessionId = Original.TaskId.ToString() }, default);
        var Runner = new FakeRunner { Saved = Saved };
        (await Execution(Store, Runner).ExecuteAsync(Delivery(Original), default)).Status.Should().Be(Expected);
        Runner.Runs.Should().Be(0);
    }

    [Fact]
    public async Task CancelAndLegacyNeverExecute()
    {
        var (Store, _) = await SetupAsync();
        var Original = Job();
        await Store.CreateAsync(Original, default);
        var Runner = new FakeRunner();
        var Service = Execution(Store, Runner);
        (await Service.CancelAsync(Original.TaskId, default)).Should().BeTrue();
        (await Service.ExecuteAsync(Delivery(Original), default)).Status.Should().Be("Cancelled");
        var Unknown = Job();
        (await Service.ExecuteAsync(Delivery(Unknown), default)).Status.Should().Be("Blocked");
        (await Store.GetAsync(Unknown.TaskId, default))!.ActorId.Should().BeNull();
        Runner.Runs.Should().Be(0);
    }

    [Fact]
    public async Task PreExecutionFailureRetriesButEffectsAreNeverRepeatedAfterFailure()
    {
        var (Store, _) = await SetupAsync();
        var Original = Job();
        await Store.CreateAsync(Original, default);
        var Runner = new FakeRunner { FailPrepare = true };
        var Service = Execution(Store, Runner);
        (await Service.ExecuteAsync(Delivery(Original), default)).Status.Should().Be("Retrying");
        Runner.FailPrepare = false;
        Runner.FailRun = true;
        (await Service.ExecuteAsync(Delivery(Original), default)).Status.Should().Be("NeedsReview");
        (await Service.ExecuteAsync(Delivery(Original), default)).Status.Should().Be("NeedsReview");
        Runner.Runs.Should().Be(1);
        (await Store.GetAttemptsAsync(Original.TaskId, default)).Select(A => A.Status).Should().Equal("Retrying", "NeedsReview");
    }

    private sealed class FakeRunner : IScheduledJobRunner
    {
        public int Runs;
        public bool FailPrepare;
        public bool FailRun;
        public string? Saved;
        public string? Instruction;
        public AgentAccessContext? Access;
        public Task PrepareAsync(Guid SessionId, AgentAccessContext Access, CancellationToken Token) =>
            FailPrepare ? Task.FromException(new HttpRequestException("Synthetic transient failure")) : Task.CompletedTask;
        public Task<string?> RunAsync(Guid SessionId, AgentAccessContext Context, string Text, CancellationToken Token)
        {
            Runs++;
            Instruction = Text;
            Access = Context;
            if (FailRun) throw new HttpRequestException("Failure after effect");
            Saved = "Synthetic result";
            return Task.FromResult<string?>(Saved);
        }
        public Task<string?> RecoverAsync(Guid SessionId, CancellationToken Token) => Task.FromResult(Saved);
    }

    [Fact]
    public async Task CreationIsDurableAndDeduplicatedWithOutbox()
    {
        var (Store, Options) = await SetupAsync();
        var Original = Job();
        await Store.CreateAsync(Original, default);
        await Store.CreateAsync(Original with { Instruction = "Forged replacement" }, default);
        var Replacement = new ScheduledJobStore(Microsoft.Extensions.Options.Options.Create(Options));
        (await Replacement.GetAsync(Original.TaskId, default)).Should().Be(Original);
        await using var Connection = await Store.OpenAsync(default);
        await using var Count = new NpgsqlCommand($"SELECT count(*) FROM {Options.Schema}.coach_call_outbox", Connection);
        (await Count.ExecuteScalarAsync()).Should().Be(1L);
        (await Store.ListAsync("other", null, null, null, default)).Should().BeEmpty();
        (await Store.ListAsync("owner", "other", null, null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task AdvisoryLockSerializesInstancesAndReleasesOnDisconnect()
    {
        var (Store, _) = await SetupAsync();
        var Id = Guid.NewGuid();
        await using var First = await Store.OpenAsync(default);
        await using var Second = await Store.OpenAsync(default);
        (await Store.TryLockAsync(First, Id, default)).Should().BeTrue();
        (await Store.TryLockAsync(Second, Id, default)).Should().BeFalse();
        await Store.UnlockAsync(First, Id);
        (await Store.TryLockAsync(Second, Id, default)).Should().BeTrue();
        await Store.UnlockAsync(Second, Id);
    }

    [Fact]
    public async Task CurrentPolicyRevocationAndAssignmentsOverrideStoredAuthority()
    {
        var Policy = new Mock<IScheduledActorPolicy>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, default)).ReturnsAsync("Owner");
        var Assignments = new Mock<ICoachAssignmentStore>();
        Assignments.Setup(A => A.GetAssignedProfilesAsync("coach", "coach@example.test", default)).ReturnsAsync(["owner"]);
        var Permissions = new Mock<IToolAccessStore>();
        Permissions.Setup(P => P.GetRolePermissionsAsync(It.IsAny<string>(), default)).ReturnsAsync(new Dictionary<string, bool>());
        var Registry = new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<Microsoft.Extensions.AI.AIFunction>()));
        var Auth = new ScheduledJobAuthorization(Policy.Object, Assignments.Object, new ToolAccessService(Permissions.Object, Registry));
        var Original = Job();
        (await Auth.ForExecutionAsync(Original, default)).Should().NotBeNull();
        (await Auth.ForExecutionAsync(Original with { SubjectProfileId = "other" }, default)).Should().BeNull();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, default)).ReturnsAsync((string?)null);
        (await Auth.ForExecutionAsync(Original, default)).Should().BeNull();
        Policy.Setup(P => P.ResolveRoleAsync("coach", "coach@example.test", default)).ReturnsAsync("Coach");
        var CoachJob = Original with { ActorId = "coach", ActorEmail = "coach@example.test" };
        (await Auth.ForExecutionAsync(CoachJob, default)).Should().BeNull();
        Permissions.Setup(P => P.GetRolePermissionsAsync("Coach", default)).ReturnsAsync(new Dictionary<string, bool> { [AgentToolKeys.ScheduleAgentTask] = true });
        (await Auth.ForExecutionAsync(CoachJob, default)).Should().NotBeNull();
        Assignments.Setup(A => A.GetAssignedProfilesAsync("coach", "coach@example.test", default)).ReturnsAsync([]);
        (await Auth.ForExecutionAsync(CoachJob, default)).Should().BeNull();
        (await Auth.ForExecutionAsync(Original with { ActorId = null }, default)).Should().BeNull();
    }
}
