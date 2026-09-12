namespace PersonalAgent.Web.Services;

public sealed record ScheduledJobResponse(
    Guid TaskId, string? ActorId, string? ActorEmail, string SubjectProfileId,
    string Instruction, DateTimeOffset CreatedAt, DateTimeOffset ExecuteAt,
    string Status, string? Outcome, string? SourceSessionId, string? SessionId,
    bool NotifyOnCompletion, Guid CorrelationId, int AttemptCount, DateTimeOffset UpdatedAt)
{
    public string JobType { get; init; } = "AgentTask";
    public ScheduledNotificationResponse? Notification { get; init; }
}
public sealed record ScheduledNotificationResponse(string Title, string Body, string? DeepLink);
public sealed record ScheduledJobAttemptResponse(int Number, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string? Outcome);
public sealed record ScheduledJobDetailResponse(ScheduledJobResponse Job, IReadOnlyList<ScheduledJobAttemptResponse> Attempts);
