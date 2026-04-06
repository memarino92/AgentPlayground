namespace PersonalAgent.Mobile.Models;

public record PendingApprovalNotification(Guid ApprovalId, string SessionId, string ToolName, string ActionSummary, DateTimeOffset ExpiresAt);
