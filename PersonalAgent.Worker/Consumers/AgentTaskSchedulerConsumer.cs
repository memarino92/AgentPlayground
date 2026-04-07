using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Commands;
using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Worker.Consumers;

internal class AgentTaskSchedulerConsumer(ILogger<AgentTaskSchedulerConsumer> logger) : IConsumer<AgentTaskScheduled>
{
    private static readonly Uri AgentTaskExecutorEndpoint = new($"queue:{MessagingEndpointNames.AgentTaskExecutor}");

    public async Task Consume(ConsumeContext<AgentTaskScheduled> context)
    {
        var message = context.Message;
        var command = new ExecuteAgentTask
        {
            TaskId = message.TaskId,
            TenantId = message.TenantId,
            UserId = message.UserId,
            CorrelationId = message.CorrelationId,
            ExecuteAtUtc = message.ExecuteAtUtc,
            Instruction = message.Instruction,
            NotifyOnCompletion = message.NotifyOnCompletion
        };

        if (message.ExecuteAtUtc <= DateTimeOffset.UtcNow)
        {
            await context.Send(AgentTaskExecutorEndpoint, command);
            logger.LogInformation(
                "Sent agent task {TaskId} immediately for tenant {TenantId}, user {UserId}",
                message.TaskId,
                message.TenantId,
                message.UserId);
            return;
        }

        var delay = message.ExecuteAtUtc - DateTimeOffset.UtcNow;
        await context.ScheduleSend(AgentTaskExecutorEndpoint, delay, command, cancellationToken: context.CancellationToken);
        logger.LogInformation(
            "Scheduled agent task {TaskId} for {ExecuteAtUtc} (tenant {TenantId}, user {UserId})",
            message.TaskId,
            message.ExecuteAtUtc,
            message.TenantId,
            message.UserId);
    }
}
