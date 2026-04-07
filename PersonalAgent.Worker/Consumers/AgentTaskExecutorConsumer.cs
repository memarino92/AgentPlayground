using AgentPlayground.Contracts.Messaging.Events;
using AgentPlayground.Contracts.Messaging.Commands;
using MassTransit;

namespace PersonalAgent.Worker.Consumers;

internal class AgentTaskExecutorConsumer(ILogger<AgentTaskExecutorConsumer> logger) : IConsumer<ExecuteAgentTask>
{
    public async Task Consume(ConsumeContext<ExecuteAgentTask> context)
    {
        var message = context.Message;
        logger.LogInformation(
            "Executing agent task {TaskId} for tenant {TenantId}, user {UserId}: {Instruction}",
            message.TaskId,
            message.TenantId,
            message.UserId,
            message.Instruction);

        if (!message.NotifyOnCompletion) return;

        var notification = new NotificationRequested
        {
            NotificationId = Guid.NewGuid(),
            TenantId = message.TenantId,
            UserId = message.UserId,
            CorrelationId = message.CorrelationId,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            ExecuteAtUtc = DateTimeOffset.UtcNow,
            Title = "Scheduled task complete",
            Body = message.Instruction,
            Source = "Worker"
        };

        await context.Publish(notification, context.CancellationToken);
        logger.LogInformation(
            "Published completion notification for agent task {TaskId}",
            message.TaskId);
    }
}
