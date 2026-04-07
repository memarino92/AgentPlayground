using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class PushNotificationService(IOptions<PushNotificationsOptions> options, ILogger<PushNotificationService> logger)
{
    private readonly PushNotificationsOptions _options = options.Value;
    private FirebaseMessaging? _messaging;

    public bool IsEnabled => _options.Enabled;

    public async Task<int> SendToDevicesAsync(string profileId, string title, string body, IReadOnlyDictionary<string, string>? data, IReadOnlyList<PersistedMobileDeviceToken> deviceTokens, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Push notifications are disabled. Skipping send for profile {ProfileId}", profileId);
            return 0;
        }

        if (deviceTokens.Count is 0)
        {
            logger.LogInformation("No device tokens found for profile {ProfileId}", profileId);
            return 0;
        }

        var messaging = GetOrCreateMessagingClient();
        var sentCount = 0;

        foreach (var device in deviceTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var message = new Message
            {
                Token = device.PushToken,
                Notification = new Notification
                {
                    Title = title,
                    Body = body
                },
                Data = data is not null ? new Dictionary<string, string>(data) : null,
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ChannelId = _options.AndroidChannelId
                    }
                }
            };

            try
            {
                _ = await messaging.SendAsync(message, cancellationToken);
                sentCount++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to send push notification for profile {ProfileId}, device {DeviceId}",
                    profileId,
                    device.DeviceId);
            }
        }

        logger.LogInformation(
            "Sent {SentCount} push notifications for profile {ProfileId}",
            sentCount,
            profileId);

        return sentCount;
    }

    private FirebaseMessaging GetOrCreateMessagingClient()
    {
        if (_messaging is not null) return _messaging;

        var appName = "personal-agent-push";
        FirebaseApp? app = null;
        try
        {
            app = FirebaseApp.GetInstance(appName);
        }
        catch
        {
            // Create below when no named instance exists.
        }

        if (app is null)
        {
            var credential = BuildCredential();
            app = FirebaseApp.Create(new AppOptions
            {
                Credential = credential,
                ProjectId = _options.FirebaseProjectId
            }, appName);
        }

        _messaging = FirebaseMessaging.GetMessaging(app);
        return _messaging;
    }

    private GoogleCredential BuildCredential()
    {
        if (!string.IsNullOrWhiteSpace(_options.ServiceAccountJson))
            return GoogleCredential.FromJson(_options.ServiceAccountJson);

        if (!string.IsNullOrWhiteSpace(_options.ServiceAccountPath))
            return GoogleCredential.FromFile(_options.ServiceAccountPath);

        throw new InvalidOperationException("Push notifications are enabled but no Firebase service account credentials were configured.");
    }
}
