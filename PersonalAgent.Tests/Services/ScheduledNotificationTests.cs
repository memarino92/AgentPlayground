using AgentPlayground.Contracts.Messaging.Commands;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class ScheduledNotificationTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    private readonly Mock<IScheduledActorPolicy> Policy = new();
    private readonly Mock<IToolAccessStore> Permissions = new();

    private ScheduledJobAuthorization Authorization()
    {
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync("Owner");
        Permissions.Setup(P => P.GetRolePermissionsAsync("Owner", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool> { [AgentToolKeys.ScheduleAgentTask] = false, [AgentToolKeys.ScheduleNotification] = true });
        var Registry = new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<Microsoft.Extensions.AI.AIFunction>()));
        return new(Policy.Object, Mock.Of<ICoachAssignmentStore>(), new ToolAccessService(Permissions.Object, Registry));
    }

    private static ScheduleNotificationRequest Request(string User = "owner") => new()
    {
        TenantId = "default", UserId = User, Title = "Reminder title", Body = "Reminder body",
        ExecuteAt = DateTimeOffset.UtcNow.AddSeconds(-1), DeepLink = "/chat"
    };

    private static ExecuteAgentTask Delivery(Guid Id) => new()
    {
        TaskId = Id, CorrelationId = Guid.NewGuid(), TenantId = "forged", UserId = "other", Instruction = "Forged content", ExecuteAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ToolPersistsNotificationWithIdentityAndDispatchesWithoutAgentOrDuplicatePush()
    {
        var (Store, Options) = await new ScheduledJobTests(Database).SetupAsync();
        var Auth = Authorization();
        var Scheduler = new SchedulingService(NullLogger<SchedulingService>.Instance, Store, Auth);
        var Bus = new Mock<MassTransit.IBus>(MockBehavior.Strict);
        var Events = new AgentEventService(Bus.Object, Scheduler, NullLogger<AgentEventService>.Instance);
        var Access = new AgentAccessContext("owner", "Owner", "owner") { SessionId = "source-chat" };
        var Result = await Events.ScheduleNotificationToolAsync(Access, "Reminder title", "Reminder body", executeAt: DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O"));
        var Job = (await Store.ListAsync("owner", null, null, null, default)).Should().ContainSingle().Which;
        Result.Should().Contain(Job.TaskId.ToString());
        Job.JobType.Should().Be("Notification");
        Job.ActorId.Should().Be("owner");
        Job.SourceSessionId.Should().Be("source-chat");
        Job.Notification.Should().Be(new ScheduledNotification("Reminder title", "Reminder body", null));
        Job.NotifyOnCompletion.Should().BeFalse();
        var Sender = new Mock<IScheduledNotificationSender>(MockBehavior.Strict);
        Sender.Setup(S => S.SendAsync(It.Is<ScheduledJob>(J => J.TaskId == Job.TaskId && J.Notification == Job.Notification && J.SubjectProfileId == "owner"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduledJobExecutionResponse("Completed", "Accepted by provider"));
        var Runner = new Mock<IScheduledJobRunner>(MockBehavior.Strict);
        var Executor = new ScheduledJobExecutionService(Store, Auth, Runner.Object, NullLogger<ScheduledJobExecutionService>.Instance, Sender.Object);
        (await Executor.ExecuteAsync(Delivery(Job.TaskId), default)).Status.Should().Be("Completed");
        var Restarted = new ScheduledJobExecutionService(new(Microsoft.Extensions.Options.Options.Create(Options)), Auth, Runner.Object, NullLogger<ScheduledJobExecutionService>.Instance, Sender.Object);
        (await Restarted.ExecuteAsync(Delivery(Job.TaskId), default)).Summary.Should().Be("Accepted by provider");
        Sender.Verify(S => S.SendAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Once);
        Runner.VerifyNoOtherCalls();
        (await Store.GetAsync(Job.TaskId, default))!.SessionId.Should().BeNull();
        (await Store.GetAttemptsAsync(Job.TaskId, default)).Should().ContainSingle().Which.Status.Should().Be("Completed");
        await using var Connection = await Store.OpenAsync(default);
        await using var Count = new NpgsqlCommand($"SELECT count(*) FROM {Options.Schema}.coach_call_outbox WHERE message_type = 'NotificationRequested'", Connection);
        (await Count.ExecuteScalarAsync()).Should().Be(0L);
    }

    [Theory]
    [InlineData("cancel", "Cancelled")]
    [InlineData("revokeActor", "Blocked")]
    [InlineData("revokeTool", "Blocked")]
    [InlineData("future", "Scheduled")]
    [InlineData("interrupted", "NeedsReview")]
    public async Task GuardedNotificationNeverSends(string Scenario, string Expected)
    {
        var (Store, _) = await new ScheduledJobTests(Database).SetupAsync();
        var Auth = Authorization();
        var Job = ScheduledJobTests.Job() with { JobType = "Notification", Notification = new("Title", "Body", null), NotifyOnCompletion = false };
        if (Scenario == "future") Job = Job with { ExecuteAt = DateTimeOffset.UtcNow.AddHours(1) };
        if (Scenario == "interrupted") Job = Job with { Status = "Running" };
        await Store.CreateAsync(Job, default);
        var Sender = new Mock<IScheduledNotificationSender>(MockBehavior.Strict);
        var Executor = new ScheduledJobExecutionService(Store, Auth, Mock.Of<IScheduledJobRunner>(), NullLogger<ScheduledJobExecutionService>.Instance, Sender.Object);
        if (Scenario == "cancel") (await Executor.CancelAsync(Job.TaskId, default)).Should().BeTrue();
        if (Scenario == "revokeActor") Policy.Setup(P => P.ResolveRoleAsync("owner", null, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        if (Scenario == "revokeTool") Permissions.Setup(P => P.GetRolePermissionsAsync("Owner", It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, bool> { [AgentToolKeys.ScheduleNotification] = false });
        (await Executor.ExecuteAsync(Delivery(Job.TaskId), default)).Status.Should().Be(Expected);
        Sender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ConcurrentDeliveryAndUncertainSendDoNotRepeatEffects()
    {
        var (Store, _) = await new ScheduledJobTests(Database).SetupAsync();
        var Auth = Authorization();
        var Created = await new SchedulingService(NullLogger<SchedulingService>.Instance, Store, Auth)
            .ScheduleNotificationAsync(Request(), new("owner", "Owner", "owner"));
        var Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Sender = new Mock<IScheduledNotificationSender>();
        Sender.Setup(S => S.SendAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            Started.SetResult();
            await Release.Task;
            throw new HttpRequestException("Uncertain provider response");
        });
        var Executor = new ScheduledJobExecutionService(Store, Auth, Mock.Of<IScheduledJobRunner>(), NullLogger<ScheduledJobExecutionService>.Instance, Sender.Object);
        var First = Executor.ExecuteAsync(Delivery(Created.Id), default);
        await Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            (await Executor.ExecuteAsync(Delivery(Created.Id), default)).Status.Should().Be("Running");
            (await Executor.CancelAsync(Created.Id, default)).Should().BeFalse();
        }
        finally { Release.SetResult(); }
        (await First).Status.Should().Be("NeedsReview");
        (await Executor.ExecuteAsync(Delivery(Created.Id), default)).Status.Should().Be("NeedsReview");
        Sender.Verify(S => S.SendAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreationRejectsSpoofedSubjectAndRevokedPermission()
    {
        var (Store, _) = await new ScheduledJobTests(Database).SetupAsync();
        var Auth = Authorization();
        var Scheduler = new SchedulingService(NullLogger<SchedulingService>.Instance, Store, Auth);
        var Access = new AgentAccessContext("owner", "Owner", "owner");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Scheduler.ScheduleNotificationAsync(Request("other"), Access));
        Permissions.Setup(P => P.GetRolePermissionsAsync("Owner", It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<string, bool> { [AgentToolKeys.ScheduleNotification] = false });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Scheduler.ScheduleNotificationAsync(Request(), Access));
        (await Store.ListAsync("owner", null, null, null, default)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(true, "No registered devices")]
    public async Task UnavailablePushReportsFailure(bool Enabled, string Outcome)
    {
        var Devices = new Mock<IAgentApprovalStore>();
        Devices.Setup(D => D.GetMobileDeviceTokensAsync("owner", It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var Push = new PushNotificationService(Options.Create(new PushNotificationsOptions { Enabled = Enabled }), NullLogger<PushNotificationService>.Instance);
        var Job = ScheduledJobTests.Job() with { JobType = "Notification", Notification = new("Title", "Body", null) };
        var Result = await new ScheduledNotificationSender(Devices.Object, Push).SendAsync(Job, default);
        Result.Status.Should().Be("Failed");
        Result.Summary.Should().Contain(Outcome);
        Devices.Verify(D => D.GetMobileDeviceTokensAsync(It.Is<string>(P => P != "owner"), It.IsAny<CancellationToken>()), Times.Never);
    }
}


