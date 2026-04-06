namespace AgentPlayground.Contracts.Messaging.Events;

public record DevicePushNotificationRequested
{
    public required Guid NotificationId { get; init; }
    public required DateTimeOffset RequestedAt { get; init; }
    public required string ProfileId { get; init; }
    public required string NotificationType { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public IReadOnlyDictionary<string, string>? Data { get; init; }
}
