using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;

namespace PersonalAgent.Mobile;

[BroadcastReceiver(Enabled = true, Exported = false)]
[IntentFilter([ActionPushData])]
public class FirebaseNotificationReceiver : BroadcastReceiver
{
    public const string ActionPushData = "com.personalagent.mobile.PUSH_DATA";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            context.CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted) return;

        var approvalIdRaw = intent.GetStringExtra("approvalId");
        if (!Guid.TryParse(approvalIdRaw, out var approvalId)) return;

        var sessionId = intent.GetStringExtra("sessionId") ?? string.Empty;
        var toolName = intent.GetStringExtra("toolName") ?? "UnknownTool";
        var actionSummary = intent.GetStringExtra("actionSummary") ?? "Agent requires approval";
        var expiresAtRaw = intent.GetStringExtra("expiresAt");
        var expiresAt = DateTimeOffset.TryParse(expiresAtRaw, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddMinutes(5);

        NotificationRoutingService.Current?.RoutePendingApproval(new PendingApprovalNotification(approvalId, sessionId, toolName, actionSummary, expiresAt));

        EnsureChannel(context);
        var builder = new Notification.Builder(context, "agent-approval-high")
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetContentTitle("Agent approval required")
            .SetContentText(actionSummary)
            .SetAutoCancel(true);

        var notificationManager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        notificationManager?.Notify((int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % int.MaxValue), builder.Build());
    }

    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var manager = (NotificationManager?)context.GetSystemService(Context.NotificationService);
        if (manager?.GetNotificationChannel("agent-approval-high") is not null) return;

        var channel = new NotificationChannel("agent-approval-high", "Agent Approvals", NotificationImportance.High)
        {
            Description = "Approvals for sensitive agent actions"
        };
        manager?.CreateNotificationChannel(channel);
    }
}
