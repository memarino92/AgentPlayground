namespace AgentPlayground.Contracts.Messaging.Events;

public record AgentGeneratedTestMessage
{
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string GeneratedBy { get; init; }
    public required string PromptSummary { get; init; }
    public required string Message { get; init; }
}
