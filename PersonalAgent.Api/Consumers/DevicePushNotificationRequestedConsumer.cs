using PersonalAgent.Contracts.Messaging.Events;
using MassTransit;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Consumers;

internal class DevicePushNotificationRequestedConsumer(IAgentApprovalStore approvalStore, PushNotificationService pushNotificationService, ILogger<DevicePushNotificationRequestedConsumer> logger) : IConsumer<DevicePushNotificationRequested>
{
    public async Task Consume(ConsumeContext<DevicePushNotificationRequested> context)
    {
        var message = context.Message;
        var tokens = await approvalStore.GetMobileDeviceTokensAsync(message.ProfileId, context.CancellationToken);

        var sentCount = await pushNotificationService.SendToDevicesAsync(
            message.ProfileId,
            message.Title,
            message.Body,
            message.Data,
            tokens,
            context.CancellationToken);

        logger.LogInformation(
            "Processed push notification request {NotificationId} for profile {ProfileId}, delivered to {SentCount} devices",
            message.NotificationId,
            message.ProfileId,
            sentCount);
    }
}
