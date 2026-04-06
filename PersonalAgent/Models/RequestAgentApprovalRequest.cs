namespace PersonalAgent.Models;

internal record RequestAgentApprovalRequest(string ProfileId, string SessionId, string ToolName, string ActionSummary, string RequestedBy, int? ExpiresInMinutes);
