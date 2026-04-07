using AgentPlayground.Contracts.Messaging.Commands;
using PersonalAgent.Worker.Configuration;
using System.Net.Http.Json;

namespace PersonalAgent.Worker.Services;

internal class AgentTaskExecutionService(IHttpClientFactory httpClientFactory, ILogger<AgentTaskExecutionService> logger) : IAgentTaskExecutionService
{
    public async Task<AgentTaskExecutionResult> ExecuteAsync(ExecuteAgentTask task, CancellationToken cancellationToken = default)
    {
        var profileId = $"{task.TenantId}:{task.UserId}";
        var client = httpClientFactory.CreateClient("PersonalAgentApi");

        try
        {
            var sessionResponse = await client.PostAsJsonAsync("/api/sessions", new
            {
                profileId,
                modelId = "gpt-4o-mini"
            }, cancellationToken);

            if (!sessionResponse.IsSuccessStatusCode)
            {
                var error = await sessionResponse.Content.ReadAsStringAsync(cancellationToken);
                return new AgentTaskExecutionResult(false, $"Failed to create task session: {error}");
            }

            var sessionPayload = await sessionResponse.Content.ReadFromJsonAsync<SessionResponse>(cancellationToken);
            if (sessionPayload?.SessionId is null)
                return new AgentTaskExecutionResult(false, "Failed to create task session: missing session id.");

            var messageResponse = await client.PostAsJsonAsync($"/api/sessions/{sessionPayload.SessionId}/messages", new
            {
                profileId,
                message = task.Instruction
            }, cancellationToken);

            if (!messageResponse.IsSuccessStatusCode)
            {
                var error = await messageResponse.Content.ReadAsStringAsync(cancellationToken);
                return new AgentTaskExecutionResult(false, $"Failed to execute task instruction: {error}");
            }

            var messagePayload = await messageResponse.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken);
            if (messagePayload?.Response is null)
                return new AgentTaskExecutionResult(false, "Task ran but returned no response.");

            var trimmed = messagePayload.Response.Trim();
            var summary = trimmed.Length > 300 ? $"{trimmed[..300]}..." : trimmed;
            logger.LogInformation(
                "Executed agent task {TaskId} for tenant {TenantId}, user {UserId} via PersonalAgent API session {SessionId}",
                task.TaskId,
                task.TenantId,
                task.UserId,
                sessionPayload.SessionId);

            return new AgentTaskExecutionResult(true, summary);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Error executing agent task {TaskId} for tenant {TenantId}, user {UserId}",
                task.TaskId,
                task.TenantId,
                task.UserId);
            return new AgentTaskExecutionResult(false, ex.Message);
        }
    }

    private sealed record SessionResponse(string SessionId, string ModelId, string Message);
    private sealed record MessageResponse(string SessionId, string Response);
}
