using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;
using Xunit;

namespace PersonalAgent.Mobile.Tests;

public class ApprovalNotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsePreservesApprovalAndAccountDetails()
    {
        var Data = Payload();
        var Approval = Assert.IsType<PendingApprovalNotification>(ApprovalNotificationParser.Parse(Data));
        Assert.Equal(Guid.Parse(Data["approvalId"]), Approval.ApprovalId);
        Assert.Equal("owner", Approval.ProfileId);
        Assert.Equal("session", Approval.SessionId);
        Assert.Equal("send-email", Approval.ToolName);
        Assert.Equal("Send the draft", Approval.ActionSummary);
        Assert.Equal("agent", Approval.RequestedBy);
        Assert.Equal(Now.AddMinutes(5), Approval.ExpiresAt);
    }

    [Theory]
    [InlineData("notificationType", "reminder")]
    [InlineData("profileId", "")]
    [InlineData("approvalId", "invalid")]
    [InlineData("approvalId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("expiresAt", "invalid")]
    public void InvalidPayloadCannotBecomeAnApproval(string Key, string Value)
    {
        var Data = Payload();
        Data[Key] = Value;
        Assert.Null(ApprovalNotificationParser.Parse(Data));
    }

    [Theory]
    [InlineData("notificationType")]
    [InlineData("profileId")]
    [InlineData("approvalId")]
    [InlineData("expiresAt")]
    public void MissingRequiredDataIsRejected(string Key)
    {
        var Data = Payload();
        Data.Remove(Key);
        Assert.Null(ApprovalNotificationParser.Parse(Data));
    }

    [Fact]
    public void MultipleRequestsRemainQueuedAndDuplicatesDoNotReopenReview()
    {
        var Routing = new NotificationRoutingService(new TestClock());
        var First = Approval("owner");
        var Second = Approval("owner");
        var Events = 0;
        Routing.PendingApprovalReceived += (_, _) => Events++;
        Routing.RoutePendingApproval(First);
        Routing.RoutePendingApproval(Second);
        Routing.RoutePendingApproval(First);
        Assert.Equal(2, Events);
        Assert.Equal(First, Routing.GetNextPendingApproval("owner"));
        Routing.Remove(First.ApprovalId);
        Assert.Equal(Second, Routing.GetNextPendingApproval("owner"));
        Routing.Remove(Second.ApprovalId);
        Assert.Null(Routing.GetNextPendingApproval("owner"));
    }

    [Fact]
    public void TappingAnAlreadyQueuedRequestCanReopenDeferredReviewWithoutDuplicatingIt()
    {
        var Routing = new NotificationRoutingService(new TestClock());
        var Pending = Approval("owner");
        var Events = 0;
        Routing.PendingApprovalReceived += (_, _) => Events++;
        Routing.RoutePendingApproval(Pending);
        Routing.RoutePendingApproval(Pending, RequestReview: true);
        Assert.Equal(2, Events);
        Assert.Equal(Pending, Routing.GetNextPendingApproval("owner"));
        Routing.Remove(Pending.ApprovalId);
        Assert.Null(Routing.GetNextPendingApproval("owner"));
    }

    [Fact]
    public void NotificationTapReviewsThatRequestBeforeOtherQueuedRequests()
    {
        var Routing = new NotificationRoutingService(new TestClock());
        var First = Approval("owner");
        var Tapped = Approval("owner");
        Routing.RoutePendingApproval(First);
        Routing.RoutePendingApproval(Tapped);
        Routing.RoutePendingApproval(Tapped, RequestReview: true);
        Assert.Equal(Tapped, Routing.GetNextPendingApproval("owner"));
        Routing.Remove(Tapped.ApprovalId);
        Assert.Equal(First, Routing.GetNextPendingApproval("owner"));
    }

    [Fact]
    public void OtherProfilesCannotConsumeTheRequest()
    {
        var Routing = new NotificationRoutingService(new TestClock());
        var Pending = Approval("owner");
        Routing.RoutePendingApproval(Pending);
        Assert.Null(Routing.GetNextPendingApproval("coach"));
        Assert.Equal(Pending, Routing.GetNextPendingApproval("OWNER"));
    }

    [Fact]
    public void ExpiryIsCheckedBothOnReceiptAndBeforeReview()
    {
        var Clock = new TestClock();
        var Routing = new NotificationRoutingService(Clock);
        var Events = 0;
        Routing.PendingApprovalReceived += (_, _) => Events++;
        Routing.RoutePendingApproval(Approval("owner") with { ExpiresAt = Now });
        Assert.Equal(0, Events);
        Routing.RoutePendingApproval(Approval("owner"));
        Assert.NotNull(Routing.GetNextPendingApproval("owner"));
        Clock.UtcNow = Now.AddMinutes(5);
        Assert.Null(Routing.GetNextPendingApproval("owner"));
    }

    [Fact]
    public void DeferredOrFailedReviewRetainsTheSameRequest()
    {
        var Routing = new NotificationRoutingService(new TestClock());
        var Pending = Approval("owner");
        Routing.RoutePendingApproval(Pending);
        Assert.Equal(Pending, Routing.GetNextPendingApproval("owner"));
        Assert.Equal(Pending, Routing.GetNextPendingApproval("owner"));
    }

    private static PendingApprovalNotification Approval(string Profile) => new(Guid.NewGuid(), Profile, "session", "send-email", "Send the draft", "agent", Now.AddMinutes(5));

    private static Dictionary<string, string> Payload() => new()
    {
        ["notificationType"] = "agent-approval", ["approvalId"] = Guid.NewGuid().ToString(),
        ["profileId"] = "owner", ["sessionId"] = "session", ["toolName"] = "send-email",
        ["actionSummary"] = "Send the draft", ["requestedBy"] = "agent", ["expiresAt"] = Now.AddMinutes(5).ToString("O")
    };

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
