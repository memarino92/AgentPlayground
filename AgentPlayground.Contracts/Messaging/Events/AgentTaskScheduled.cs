namespace AgentPlayground.Contracts.Messaging.Events;

public record AgentTaskScheduled
{
    public required Guid TaskId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset RequestedAtUtc { get; init; }
    public required DateTimeOffset ExecuteAtUtc { get; init; }
    public required string Instruction { get; init; }
    public bool NotifyOnCompletion { get; init; } = true;
}
