using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal interface IScheduledNotificationSender
{
    Task<ScheduledJobExecutionResponse> SendAsync(ScheduledJob Job, CancellationToken Token);
}

internal sealed class ScheduledNotificationSender(IAgentApprovalStore Devices, PushNotificationService Push) : IScheduledNotificationSender
{
    public async Task<ScheduledJobExecutionResponse> SendAsync(ScheduledJob Job, CancellationToken Token)
    {
        var Notification = Job.Notification ?? throw new InvalidOperationException("Notification payload is missing.");
        if (!Push.IsEnabled) return new("Failed", "Push notifications are disabled. No notification was sent.");
        var Tokens = await Devices.GetMobileDeviceTokensAsync(Job.SubjectProfileId, Token);
        if (Tokens.Count == 0) return new("Failed", "No registered devices were found for this profile. Open the Android companion and register the device.");
        var Data = new Dictionary<string, string> { ["jobId"] = Job.TaskId.ToString() };
        if (!string.IsNullOrWhiteSpace(Notification.DeepLink)) Data["deepLink"] = Notification.DeepLink;
        var (Accepted, Unregistered) = await Push.SendToDevicesAsync(Job.SubjectProfileId, Notification.Title, Notification.Body, Data, Tokens, Token);
        if (Unregistered == Tokens.Count) return new("Failed", "Firebase rejected every device registration as expired. Open the Android companion and register the device again.");
        return Accepted == Tokens.Count
            ? new("Completed", $"Push provider accepted the notification for {Accepted} device(s). Device receipt is not confirmed.")
            : new("NeedsReview", $"Push provider accepted {Accepted} of {Tokens.Count} sends. Other sends failed or have an uncertain outcome; automatic replay is disabled.");
    }
}
