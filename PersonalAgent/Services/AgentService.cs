using AgentPlayground.Contracts.Messaging.Events;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class AgentService(
    AgentChatService chatService,
    AgentEventService eventService,
    AgentApprovalService approvalService,
    SchedulingService schedulingService,
    CoachCheckinService coachCheckinService)
{
    public Task<(string SessionId, string ModelId)> CreateSessionAsync(string profileId, string modelId) =>
        chatService.CreateSessionAsync(profileId, modelId);

    public Task<(string SessionId, string ModelId)> CreateSessionAsync(AgentAccessContext access, string modelId) =>
        chatService.CreateSessionAsync(access, modelId);

    public Task<SessionSummaryPage> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize) =>
        chatService.GetSessionsAsync(profileId, beforeActivityAt, beforeSessionId, pageSize);

    public Task<SessionSummaryPage> GetSessionsAsync(AgentAccessContext access, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize) =>
        chatService.GetSessionsAsync(access, beforeActivityAt, beforeSessionId, pageSize);

    public Task<string?> SendMessageAsync(string sessionId, string profileId, string message) =>
        chatService.SendMessageAsync(sessionId, profileId, message);

    public Task<string?> SendMessageAsync(string sessionId, AgentAccessContext access, string message, CancellationToken cancellationToken = default) =>
        chatService.SendMessageAsync(sessionId, access, message, cancellationToken);

    public Task<SessionConversation?> GetSessionMessagesAsync(string sessionId, string profileId) =>
        chatService.GetSessionMessagesAsync(sessionId, profileId);

    public Task<SessionConversation?> GetSessionMessagesAsync(string sessionId, AgentAccessContext access, CancellationToken cancellationToken = default) =>
        chatService.GetSessionMessagesAsync(sessionId, access, cancellationToken);

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

    public Task<ScheduleResult> ScheduleNotificationAsync(ScheduleNotificationRequest request, CancellationToken cancellationToken = default) =>
        schedulingService.ScheduleNotificationAsync(request, cancellationToken);

    public Task<ScheduleResult> ScheduleAgentTaskAsync(ScheduleAgentTaskRequest request, CancellationToken cancellationToken = default) =>
        schedulingService.ScheduleAgentTaskAsync(request, cancellationToken);

    public Task<CoachCallUploadResult> CreateCoachCheckinUploadAsync(string profileId, string originalFileName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default) =>
        coachCheckinService.CreateUploadAsync(profileId, originalFileName, mimeType, bytes, cancellationToken);

    public Task<CoachCheckinStatusResponse?> GetCoachCheckinStatusAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default) =>
        coachCheckinService.GetStatusAsync(uploadId, profileId, cancellationToken);

    public Task<CoachCheckinSummaryResponse?> GetCoachCheckinSummaryAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default) =>
        coachCheckinService.GetSummaryAsync(uploadId, profileId, cancellationToken);

    public Task<CoachCheckinTranscriptResponse?> GetCoachCheckinTranscriptAsync(Guid uploadId, CancellationToken cancellationToken = default) =>
        coachCheckinService.GetTranscriptAsync(uploadId, cancellationToken);

    public Task<CoachCheckinTranscriptResponse?> GetCoachCheckinTranscriptAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default) =>
        coachCheckinService.GetTranscriptAsync(uploadId, profileId, cancellationToken);

    public Task ApplyCoachSpeakerOverridesAsync(Guid uploadId, string profileId, IReadOnlyList<CoachSpeakerOverrideItem> overrides, CancellationToken cancellationToken = default) =>
        coachCheckinService.ApplySpeakerOverridesAsync(uploadId, profileId, overrides, cancellationToken);

    public Task<IReadOnlyList<CoachCheckinAdminItem>> GetCoachCheckinAdminItemsAsync(int limit = 100, CancellationToken cancellationToken = default) =>
        coachCheckinService.GetRecentUploadsAsync(limit, cancellationToken);

    public Task<IReadOnlyList<CoachCheckinAdminItem>> GetCoachCheckinItemsAsync(string profileId, int limit = 100, CancellationToken cancellationToken = default) =>
        coachCheckinService.GetRecentUploadsAsync(profileId, limit, cancellationToken);
}
