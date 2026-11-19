using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal class PushNotificationService(IOptions<PushNotificationsOptions> options, ILogger<PushNotificationService> logger, IAgentApprovalStore devices)
{
    private readonly PushNotificationsOptions _options = options.Value;
    private FirebaseMessaging? _messaging;

    public bool IsEnabled => _options.Enabled;

    public async Task<(int Accepted, int Unregistered)> SendToDevicesAsync(string profileId, string title, string body, IReadOnlyDictionary<string, string>? data, IReadOnlyList<PersistedMobileDeviceToken> deviceTokens, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Push notifications are disabled. Skipping send for profile {ProfileId}", profileId);
            return (0, 0);
        }

        if (deviceTokens.Count is 0)
        {
            logger.LogInformation("No device tokens found for profile {ProfileId}", profileId);
            return (0, 0);
        }

        var messaging = GetOrCreateMessagingClient();
        var sentCount = 0;
        var unregistered = 0;

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
                    Notification = CreateAndroidNotification(_options.AndroidChannelId)
                }
            };

            try
            {
                _ = await messaging.SendAsync(message, cancellationToken);
                sentCount++;
            }
            catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode == MessagingErrorCode.Unregistered)
            {
                await devices.RemoveRejectedTokenAsync(profileId, device.DeviceId, device.PushToken, cancellationToken);
                unregistered++;
                logger.LogInformation("Firebase registration expired for device {DeviceId}", device.DeviceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
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

        return (sentCount, unregistered);
    }

    internal static AndroidNotification CreateAndroidNotification(string ChannelId) => new()
    {
        ChannelId = ChannelId,
        EventTimestamp = DateTime.UtcNow
    };

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
