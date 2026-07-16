namespace AgentPlayground.Contracts.Events;

public record CoachCallProcessingFailedEvent(
    Guid UploadId,
    Guid? SessionId,
    string ProfileId,
    Guid CorrelationId,
    string Stage,
    string Error,
    DateTimeOffset FailedAtUtc);
