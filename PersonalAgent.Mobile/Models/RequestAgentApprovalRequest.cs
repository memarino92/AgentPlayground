namespace PersonalAgent.Mobile.Models;

public record RequestAgentApprovalRequest(string ProfileId, string SessionId, string ToolName, string ActionSummary, string RequestedBy, int? ExpiresInMinutes);
