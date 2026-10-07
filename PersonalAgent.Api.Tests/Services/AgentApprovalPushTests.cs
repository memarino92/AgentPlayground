using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Messaging.Events;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public class AgentApprovalPushTests
{
    [Fact]
    public async Task PushContainsTheStoredApprovalIdentityAndReviewDetails()
    {
        var Approval = new PersistedAgentApproval(Guid.NewGuid(), "owner", "session", "send-email", "Send the draft", "agent",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "pending", null, null, null);
        var Store = new Mock<IAgentApprovalStore>();
        Store.Setup(Item => Item.CreateAgentApprovalAsync("owner", "session", "send-email", "Send the draft", "agent", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Approval);
        DevicePushNotificationRequested? Notification = null;
        var Bus = new Mock<IBus>();
        Bus.Setup(Item => Item.Publish(It.IsAny<DevicePushNotificationRequested>(), It.IsAny<CancellationToken>()))
            .Callback<DevicePushNotificationRequested, CancellationToken>((Item, _) => Notification = Item).Returns(Task.CompletedTask);
        Bus.Setup(Item => Item.Publish(It.IsAny<AgentApprovalRequested>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var Service = new AgentApprovalService(Store.Object, Bus.Object, NullLogger<AgentApprovalService>.Instance);
        await Service.RequestApprovalAsync(new(" owner ", " session ", " send-email ", " Send the draft ", " agent ", 5));
        Assert.NotNull(Notification);
        Assert.Equal("owner", Notification.ProfileId);
        Assert.Equal("Send the draft", Notification.Body);
        Assert.Equal("agent-approval", Notification.Data!["notificationType"]);
        Assert.Equal(Approval.ApprovalId.ToString(), Notification.Data["approvalId"]);
        Assert.Equal(Approval.ProfileId, Notification.Data["profileId"]);
        Assert.Equal(Approval.SessionId, Notification.Data["sessionId"]);
        Assert.Equal(Approval.ToolName, Notification.Data["toolName"]);
        Assert.Equal(Approval.ActionSummary, Notification.Data["actionSummary"]);
        Assert.Equal(Approval.RequestedBy, Notification.Data["requestedBy"]);
        Assert.Equal(Approval.ExpiresAt.ToString("O"), Notification.Data["expiresAt"]);
    }

    [Fact]
    public async Task RejectedDecisionDoesNotPublishCompletion()
    {
        var Store = new Mock<IAgentApprovalStore>();
        var Bus = new Mock<IBus>();
        var Service = new AgentApprovalService(Store.Object, Bus.Object, NullLogger<AgentApprovalService>.Instance);
        Assert.Null(await Service.CompleteApprovalAsync(Guid.NewGuid(), new("owner", true, "owner", null)));
        Bus.Verify(Item => Item.Publish(It.IsAny<AgentApprovalCompleted>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public class AgentApprovalPersistenceTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task ExpiredAndCrossProfileRequestsCannotBeDecidedAndDuplicatesKeepTheFirstDecision()
    {
        var Options = Microsoft.Extensions.Options.Options.Create(new PersonalAgent.Api.Configuration.AgentMemoryOptions
        {
            ConnectionString = Fixture.ConnectionString, Schema = "approvals_" + Guid.NewGuid().ToString("N"),
            EnableSemanticMemory = false
        });
        await new AgentMemorySchemaInitializer(Options, NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        var Store = new PostgresAgentSessionStore(Options, NullLogger<PostgresAgentSessionStore>.Instance);
        var Expired = await Store.CreateAgentApprovalAsync("owner", "session", "tool", "expired action", "agent", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(await Store.CompleteAgentApprovalAsync(Expired.ApprovalId, "owner", true, "owner", null));
        Assert.Equal("pending", (await Store.GetAgentApprovalAsync(Expired.ApprovalId))!.Status);

        var Pending = await Store.CreateAgentApprovalAsync("owner", "session", "tool", "current action", "agent", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Null(await Store.CompleteAgentApprovalAsync(Pending.ApprovalId, "other", true, "other", null));
        var Completed = await Store.CompleteAgentApprovalAsync(Pending.ApprovalId, "owner", false, "owner", "Denied on phone");
        Assert.Equal("denied", Completed!.Status);
        Assert.Null(await Store.CompleteAgentApprovalAsync(Pending.ApprovalId, "owner", true, "owner", null));
        Assert.Equal("denied", (await Store.GetAgentApprovalAsync(Pending.ApprovalId))!.Status);
    }
}
