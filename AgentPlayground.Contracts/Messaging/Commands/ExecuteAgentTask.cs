namespace AgentPlayground.Contracts.Messaging.Commands;

public record ExecuteAgentTask
{
    public required Guid TaskId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset ExecuteAtUtc { get; init; }
    public required string Instruction { get; init; }
    public bool NotifyOnCompletion { get; init; } = true;
}
