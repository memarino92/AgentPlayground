using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface IAgentApprovalStore
{
    Task RegisterMobileDeviceTokenAsync(string profileId, string deviceId, string platform, string pushToken, string? appVersion, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PersistedMobileDeviceToken>> GetMobileDeviceTokensAsync(string profileId, CancellationToken cancellationToken = default);
    Task<PersistedAgentApproval> CreateAgentApprovalAsync(string profileId, string sessionId, string toolName, string actionSummary, string requestedBy, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task<PersistedAgentApproval?> CompleteAgentApprovalAsync(Guid approvalId, string profileId, bool approved, string decidedBy, string? reason, CancellationToken cancellationToken = default);
    Task<PersistedAgentApproval?> GetAgentApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default);
}
