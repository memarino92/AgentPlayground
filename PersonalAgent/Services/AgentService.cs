using AgentPlayground.Contracts.Messaging.Events;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class AgentService(AgentChatService chatService, AgentEventService eventService, AgentApprovalService approvalService)
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

    public Task RegisterMobileDeviceTokenAsync(RegisterMobileDeviceTokenRequest request, CancellationToken cancellationToken = default) =>
        approvalService.RegisterMobileDeviceTokenAsync(request, cancellationToken);

    public Task<PersistedAgentApproval> RequestApprovalAsync(RequestAgentApprovalRequest request, CancellationToken cancellationToken = default) =>
        approvalService.RequestApprovalAsync(request, cancellationToken);

    public Task<PersistedAgentApproval?> CompleteApprovalAsync(Guid approvalId, CompleteAgentApprovalRequest request, CancellationToken cancellationToken = default) =>
        approvalService.CompleteApprovalAsync(approvalId, request, cancellationToken);

    public Task<PersistedAgentApproval?> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        approvalService.GetApprovalAsync(approvalId, cancellationToken);
}
