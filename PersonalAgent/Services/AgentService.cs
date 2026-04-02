using AgentPlayground.Contracts.Messaging.Events;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class AgentService(AgentChatService chatService, AgentEventService eventService)
{
    public Task<(string SessionId, string ModelId)> CreateSessionAsync(string profileId, string modelId) =>
        chatService.CreateSessionAsync(profileId, modelId);

    public Task<SessionSummaryPage> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize) =>
        chatService.GetSessionsAsync(profileId, beforeActivityAt, beforeSessionId, pageSize);

    public Task<string?> SendMessageAsync(string sessionId, string profileId, string message) =>
        chatService.SendMessageAsync(sessionId, profileId, message);

    public Task<SessionConversation?> GetSessionMessagesAsync(string sessionId, string profileId) =>
        chatService.GetSessionMessagesAsync(sessionId, profileId);

    public Task GenerateAndPublishTestMessageAsync(TestEventRequested request, CancellationToken cancellationToken) =>
        eventService.GenerateAndPublishTestMessageAsync(request, cancellationToken);
}
