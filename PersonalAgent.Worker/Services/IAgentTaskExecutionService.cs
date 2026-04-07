using AgentPlayground.Contracts.Messaging.Commands;

namespace PersonalAgent.Worker.Services;

internal interface IAgentTaskExecutionService
{
    Task<AgentTaskExecutionResult> ExecuteAsync(ExecuteAgentTask task, CancellationToken cancellationToken = default);
}

internal record AgentTaskExecutionResult(bool Succeeded, string Summary);
