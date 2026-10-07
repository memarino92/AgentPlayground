using Microsoft.Extensions.Logging;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.CloudMessaging.EventArgs;
using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public class FirebaseCloudMessagingBridge
{
    private readonly NotificationRoutingService _notificationRoutingService;
    private readonly ILogger<FirebaseCloudMessagingBridge> _logger;
    private readonly IPushTokenProvider _pushTokenProvider;

    public FirebaseCloudMessagingBridge(NotificationRoutingService notificationRoutingService, IPushTokenProvider pushTokenProvider, ILogger<FirebaseCloudMessagingBridge> logger)
    {
        _notificationRoutingService = notificationRoutingService;
        _logger = logger;
        _pushTokenProvider = pushTokenProvider;

        if (!CrossFirebaseCloudMessaging.IsSupported) return;

        var cloudMessaging = CrossFirebaseCloudMessaging.Current;
        cloudMessaging.TokenChanged += OnTokenChanged;
        cloudMessaging.NotificationReceived += OnNotificationReceived;
        cloudMessaging.NotificationTapped += OnNotificationTapped;
        cloudMessaging.Error += OnCloudMessagingError;

    }

    public async Task InitializeAsync()
    {
        if (!CrossFirebaseCloudMessaging.IsSupported) return;
        try
        {
            var cloudMessaging = CrossFirebaseCloudMessaging.Current;
            await cloudMessaging.CheckIfValidAsync();
            var token = await cloudMessaging.GetTokenAsync();
            if (string.IsNullOrWhiteSpace(token)) return;

            _pushTokenProvider.UpdateToken(token);
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
        _pushTokenProvider.UpdateToken(e.Token);
        _logger.LogInformation("Firebase token updated");
    }

    private void OnNotificationReceived(object? sender, FCMNotificationReceivedEventArgs e) =>
        RouteApprovalNotification(e.Notification);

    private void OnNotificationTapped(object? sender, FCMNotificationTappedEventArgs e) =>
        RouteApprovalNotification(e.Notification, true);

    private void OnCloudMessagingError(object? sender, FCMErrorEventArgs e) =>
        _logger.LogWarning("Firebase cloud messaging error: {Message}", e.Message);

    private void RouteApprovalNotification(FCMNotification notification, bool RequestReview = false)
    {
        var approval = ApprovalNotificationParser.Parse(notification.Data is null ? null : new Dictionary<string, string>(notification.Data));
        if (approval is not null) _notificationRoutingService.RoutePendingApproval(approval, RequestReview);
    }
}
