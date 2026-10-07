namespace PersonalAgent.Mobile.Models;

public record PendingApprovalNotification(Guid ApprovalId, string ProfileId, string SessionId, string ToolName, string ActionSummary, string RequestedBy, DateTimeOffset ExpiresAt);
