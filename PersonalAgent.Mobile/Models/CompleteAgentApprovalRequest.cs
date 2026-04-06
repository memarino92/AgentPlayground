namespace PersonalAgent.Mobile.Models;

internal record CompleteAgentApprovalRequest(string ProfileId, bool Approved, string DecidedBy, string? Reason);
