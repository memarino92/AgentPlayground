namespace PersonalAgent.Models;

internal sealed record ScheduledJob(
    Guid TaskId, string? ActorId, string? ActorEmail, string SubjectProfileId,
    string Instruction, DateTimeOffset CreatedAt, DateTimeOffset ExecuteAt,
    string Status, string? Outcome, string? SourceSessionId, string? SessionId,
    bool NotifyOnCompletion, Guid CorrelationId, int AttemptCount,
    DateTimeOffset UpdatedAt);

internal sealed record ScheduledJobAttempt(int Number, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, string? Outcome);
internal sealed record ScheduledJobDetail(ScheduledJob Job, IReadOnlyList<ScheduledJobAttempt> Attempts);
internal sealed record ScheduledJobExecutionResponse(string Status, string? Summary);
