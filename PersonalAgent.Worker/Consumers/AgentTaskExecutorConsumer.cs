using AgentPlayground.Contracts.Messaging.Events;
using AgentPlayground.Contracts.Messaging.Commands;
using MassTransit;
using PersonalAgent.Worker.Services;

namespace PersonalAgent.Worker.Consumers;

internal class AgentTaskExecutorConsumer(IAgentTaskExecutionService taskExecutionService, ILogger<AgentTaskExecutorConsumer> logger) : IConsumer<ExecuteAgentTask>
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

        var executionResult = await taskExecutionService.ExecuteAsync(message, context.CancellationToken);

        if (!message.NotifyOnCompletion) return;

        var title = executionResult.Succeeded ? "Scheduled task complete" : "Scheduled task failed";
        var notification = new NotificationRequested
        {
            NotificationId = Guid.NewGuid(),
            TenantId = message.TenantId,
            UserId = message.UserId,
            CorrelationId = message.CorrelationId,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            ExecuteAtUtc = DateTimeOffset.UtcNow,
            Title = title,
            Body = executionResult.Summary,
            Source = "Worker"
        };

        await context.Publish(notification, context.CancellationToken);
        logger.LogInformation(
            "Published completion notification for agent task {TaskId}",
            message.TaskId);
    }
}
