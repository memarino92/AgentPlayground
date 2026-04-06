using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class AgentApprovalService(IAgentApprovalStore approvalStore, IBus bus, ILogger<AgentApprovalService> logger)
{
    public Task RegisterMobileDeviceTokenAsync(RegisterMobileDeviceTokenRequest request, CancellationToken cancellationToken = default) =>
        approvalStore.RegisterMobileDeviceTokenAsync(
            request.ProfileId.Trim(),
            request.DeviceId.Trim(),
            request.Platform.Trim().ToLowerInvariant(),
            request.PushToken.Trim(),
            string.IsNullOrWhiteSpace(request.AppVersion) ? null : request.AppVersion.Trim(),
            cancellationToken);

    public async Task<PersistedAgentApproval> RequestApprovalAsync(RequestAgentApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var expiresInMinutes = request.ExpiresInMinutes is > 0 and <= 60
            ? request.ExpiresInMinutes.Value
            : 5;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(expiresInMinutes);

        var approval = await approvalStore.CreateAgentApprovalAsync(
            request.ProfileId.Trim(),
            request.SessionId.Trim(),
            request.ToolName.Trim(),
            request.ActionSummary.Trim(),
            request.RequestedBy.Trim(),
            expiresAt,
            cancellationToken);

        var notification = new DevicePushNotificationRequested
        {
            NotificationId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow,
            ProfileId = approval.ProfileId,
            NotificationType = "agent-approval",
            Title = "Agent approval required",
            Body = approval.ActionSummary,
            Data = new Dictionary<string, string>
            {
                ["approvalId"] = approval.ApprovalId.ToString(),
                ["sessionId"] = approval.SessionId,
                ["toolName"] = approval.ToolName,
                ["requestedBy"] = approval.RequestedBy,
                ["expiresAt"] = approval.ExpiresAt.ToString("O")
            }
        };

        await bus.Publish(notification, cancellationToken);

        logger.LogInformation(
            "Requested agent approval {ApprovalId} for profile {ProfileId}, session {SessionId}, tool {ToolName}",
            approval.ApprovalId,
            approval.ProfileId,
            approval.SessionId,
            approval.ToolName);

        await bus.Publish(new AgentApprovalRequested
        {
            ApprovalId = approval.ApprovalId,
            RequestedAt = approval.RequestedAt,
            ExpiresAt = approval.ExpiresAt,
            ProfileId = approval.ProfileId,
            SessionId = approval.SessionId,
            ToolName = approval.ToolName,
            ActionSummary = approval.ActionSummary,
            RequestedBy = approval.RequestedBy
        }, cancellationToken);

        return approval;
    }

    public async Task<PersistedAgentApproval?> CompleteApprovalAsync(Guid approvalId, CompleteAgentApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var updated = await approvalStore.CompleteAgentApprovalAsync(
            approvalId,
            request.ProfileId.Trim(),
            request.Approved,
            request.DecidedBy.Trim(),
            string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            cancellationToken);
        if (updated is null) return null;

        await bus.Publish(new AgentApprovalCompleted
        {
            ApprovalId = updated.ApprovalId,
            CompletedAt = updated.DecisionAt ?? DateTimeOffset.UtcNow,
            ProfileId = updated.ProfileId,
            SessionId = updated.SessionId,
            Approved = string.Equals(updated.Status, "approved", StringComparison.Ordinal),
            Reason = updated.Reason,
            DecidedBy = updated.DecidedBy ?? request.DecidedBy.Trim()
        }, cancellationToken);

        logger.LogInformation(
            "Completed agent approval {ApprovalId} with status {Status} for profile {ProfileId}",
            updated.ApprovalId,
            updated.Status,
            updated.ProfileId);

        return updated;
    }

    public Task<PersistedAgentApproval?> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
        approvalStore.GetAgentApprovalAsync(approvalId, cancellationToken);
}
