using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Commands;
using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Consumers;

internal class NotificationSchedulerConsumer(ILogger<NotificationSchedulerConsumer> logger) : IConsumer<NotificationRequested>
{
    private static readonly Uri PushNotificationEndpoint = new($"queue:{MessagingEndpointNames.PushNotification}");

    public async Task Consume(ConsumeContext<NotificationRequested> context)
    {
        var message = context.Message;
        var command = new SendNotification
        {
            NotificationId = message.NotificationId,
            TenantId = message.TenantId,
            UserId = message.UserId,
            CorrelationId = message.CorrelationId,
            ExecuteAtUtc = message.ExecuteAtUtc,
            Title = message.Title,
            Body = message.Body,
            DeepLink = message.DeepLink
        };

        if (message.ExecuteAtUtc <= DateTimeOffset.UtcNow)
        {
            await context.Send(PushNotificationEndpoint, command);
            logger.LogInformation(
                "Sent notification {NotificationId} immediately for tenant {TenantId}, user {UserId}",
                message.NotificationId,
                message.TenantId,
                message.UserId);
            return;
        }

        var delay = message.ExecuteAtUtc - DateTimeOffset.UtcNow;
        await context.ScheduleSend(PushNotificationEndpoint, delay, command, cancellationToken: context.CancellationToken);
        logger.LogInformation(
            "Scheduled notification {NotificationId} for {ExecuteAtUtc} (tenant {TenantId}, user {UserId})",
            message.NotificationId,
            message.ExecuteAtUtc,
            message.TenantId,
            message.UserId);
    }
}
