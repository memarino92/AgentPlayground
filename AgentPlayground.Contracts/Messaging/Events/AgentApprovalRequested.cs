namespace AgentPlayground.Contracts.Messaging.Events;

public record AgentApprovalRequested
{
    public required Guid ApprovalId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string ProfileId { get; init; }
    public required string SessionId { get; init; }
    public required string ToolName { get; init; }
    public required string ActionSummary { get; init; }
    public required string RequestedBy { get; init; }
}
