namespace PersonalAgent.Models;

internal record PersistedAgentApproval(Guid ApprovalId, string ProfileId, string SessionId, string ToolName, string ActionSummary, string RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt, string Status, DateTimeOffset? DecisionAt, string? DecidedBy, string? Reason);
