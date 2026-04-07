using Microsoft.Extensions.Logging;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.EventArgs;
using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public class FirebaseCloudMessagingBridge
{
    private readonly NotificationRoutingService _notificationRoutingService;
    private readonly ILogger<FirebaseCloudMessagingBridge> _logger;

    public FirebaseCloudMessagingBridge(NotificationRoutingService notificationRoutingService, ILogger<FirebaseCloudMessagingBridge> logger)
    {
        _notificationRoutingService = notificationRoutingService;
        _logger = logger;

        if (!CrossFirebaseCloudMessaging.IsSupported) return;

        var cloudMessaging = CrossFirebaseCloudMessaging.Current;
        cloudMessaging.TokenChanged += OnTokenChanged;
        cloudMessaging.NotificationReceived += OnNotificationReceived;
        cloudMessaging.NotificationTapped += OnNotificationTapped;
        cloudMessaging.Error += OnCloudMessagingError;

        _ = InitializeAsync(cloudMessaging);
    }

    private async Task InitializeAsync(IFirebaseCloudMessaging cloudMessaging)
    {
        try
        {
            await cloudMessaging.CheckIfValidAsync();
            var token = await cloudMessaging.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token)) return;

            Preferences.Default.Set("PushToken", token);
            _logger.LogInformation("Firebase token initialized");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize Firebase cloud messaging");
        }
    }

    private void OnTokenChanged(object? sender, FCMTokenChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Token)) return;
        Preferences.Default.Set("PushToken", e.Token);
        _logger.LogInformation("Firebase token updated");
    }

    private void OnNotificationReceived(object? sender, FCMNotificationReceivedEventArgs e) =>
        RouteApprovalNotification(e.Notification);

    private void OnNotificationTapped(object? sender, FCMNotificationTappedEventArgs e) =>
        RouteApprovalNotification(e.Notification);

    private void OnCloudMessagingError(object? sender, FCMErrorEventArgs e) =>
        _logger.LogWarning("Firebase cloud messaging error: {Message}", e.Message);

    private void RouteApprovalNotification(FCMNotification notification)
    {
        var data = notification.Data;
        if (data is null || !data.TryGetValue("approvalId", out var approvalIdRaw)) return;
        if (!Guid.TryParse(approvalIdRaw, out var approvalId)) return;

        var sessionId = data.TryGetValue("sessionId", out var parsedSessionId) ? parsedSessionId : string.Empty;
        var toolName = data.TryGetValue("toolName", out var parsedToolName) ? parsedToolName : "UnknownTool";
        var actionSummary = data.TryGetValue("actionSummary", out var parsedActionSummary)
            ? parsedActionSummary
            : (string.IsNullOrWhiteSpace(notification.Body) ? "Agent requires approval" : notification.Body);
        var expiresAtRaw = data.TryGetValue("expiresAt", out var parsedExpiresAt) ? parsedExpiresAt : string.Empty;
        var expiresAt = DateTimeOffset.TryParse(expiresAtRaw, out var parsed) ? parsed : DateTimeOffset.UtcNow.AddMinutes(5);

        _notificationRoutingService.RoutePendingApproval(new PendingApprovalNotification(approvalId, sessionId, toolName, actionSummary, expiresAt));
    }
}
