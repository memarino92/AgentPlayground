using System.Net.Http.Json;
using AgentPlayground.Contracts.Messaging.Commands;

namespace PersonalAgent.Worker.Services;

internal class AgentTaskExecutionService(IHttpClientFactory HttpClientFactory) : IAgentTaskExecutionService
{
    public async Task<AgentTaskExecutionResult> ExecuteAsync(ExecuteAgentTask Task, CancellationToken CancellationToken = default)
    {
        var Client = HttpClientFactory.CreateClient("PersonalAgentApi");
        using var Response = await Client.PostAsJsonAsync($"/api/jobs/{Task.TaskId}/execute", Task, CancellationToken);
        // HTTP failures participate in retry; API persists outcomes and owns completion notifications.
        Response.EnsureSuccessStatusCode();
        var Result = await Response.Content.ReadFromJsonAsync<ExecutionResponse>(CancellationToken)
            ?? throw new HttpRequestException("Missing scheduled job execution response.");
        return new(Result.Status == "Completed", Result.Summary ?? Result.Status);
    }

    private sealed record ExecutionResponse(string Status, string? Summary);
}
