using AgentPlayground.Contracts.Messaging.Commands;
using MassTransit;
using PersonalAgent.Worker.Services;

namespace PersonalAgent.Worker.Consumers;

internal class AgentTaskExecutorConsumer(IAgentTaskExecutionService TaskExecutionService, ILogger<AgentTaskExecutorConsumer> Logger) : IConsumer<ExecuteAgentTask>
{
    public async Task Consume(ConsumeContext<ExecuteAgentTask> Context)
    {
        Logger.LogInformation("Requesting execution of scheduled job {TaskId}", Context.Message.TaskId);
        await TaskExecutionService.ExecuteAsync(Context.Message, Context.CancellationToken);
    }
}
