using AgentPlayground.Contracts.Messaging.Commands;
using MassTransit;
using PersonalAgent.Services;

namespace PersonalAgent.Consumers;

internal class PushNotificationConsumer(IAgentApprovalStore approvalStore, PushNotificationService pushNotificationService, ILogger<PushNotificationConsumer> logger) : IConsumer<SendNotification>
{
    public async Task Consume(ConsumeContext<SendNotification> context)
    {
        var message = context.Message;
        var profileId = $"{message.TenantId}:{message.UserId}";
        var tokens = await approvalStore.GetMobileDeviceTokensAsync(profileId, context.CancellationToken);
        var sentCount = await pushNotificationService.SendToDevicesAsync(
            profileId,
            message.Title,
            message.Body,
            null,
            tokens,
            context.CancellationToken);

        logger.LogInformation(
            "Processed scheduled push notification {NotificationId} for tenant {TenantId}, user {UserId}, delivered to {SentCount} devices",
            message.NotificationId,
            message.TenantId,
            message.UserId,
            sentCount);
    }
}
