namespace PersonalAgent.Mobile.Models;

public record AgentApprovalDetails(Guid ApprovalId, string ProfileId, string SessionId, string ToolName, string ActionSummary, string RequestedBy, DateTimeOffset ExpiresAt, string Status);
