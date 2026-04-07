namespace AgentPlayground.Contracts.Messaging.Commands;

public record SendNotification
{
    public required Guid NotificationId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required Guid CorrelationId { get; init; }
    public required DateTimeOffset ExecuteAtUtc { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? DeepLink { get; init; }
}
