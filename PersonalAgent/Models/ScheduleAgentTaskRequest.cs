namespace PersonalAgent.Models;

public record ScheduleAgentTaskRequest
{
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public required string Instruction { get; init; }
    public string? Delay { get; init; }
    public DateTimeOffset? ExecuteAt { get; init; }
    public string? When { get; init; }
    public string? TimeZoneId { get; init; }
    public bool NotifyOnCompletion { get; init; } = true;
    public Guid? CorrelationId { get; init; }
}
