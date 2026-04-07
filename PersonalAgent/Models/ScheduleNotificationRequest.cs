namespace PersonalAgent.Models;

public record ScheduleNotificationRequest
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public string? Delay { get; init; }
    public DateTimeOffset? ExecuteAt { get; init; }
    public string? When { get; init; }
    public string? TimeZoneId { get; init; }
    public string? DeepLink { get; init; }
    public Guid? CorrelationId { get; init; }
}
