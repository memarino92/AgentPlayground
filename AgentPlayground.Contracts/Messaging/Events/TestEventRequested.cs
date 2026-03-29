namespace AgentPlayground.Contracts.Messaging.Events;

public record TestEventRequested
{
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required string RequestedBy { get; init; }
    public required string Source { get; init; }
    public required string Message { get; init; }
}
