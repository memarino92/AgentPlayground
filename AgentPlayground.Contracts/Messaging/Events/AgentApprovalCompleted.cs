namespace AgentPlayground.Contracts.Messaging.Events;

public record AgentApprovalCompleted
{
    public required Guid ApprovalId { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public required string ProfileId { get; init; }
    public required string SessionId { get; init; }
    public required bool Approved { get; init; }
    public string? Reason { get; init; }
    public required string DecidedBy { get; init; }
}
