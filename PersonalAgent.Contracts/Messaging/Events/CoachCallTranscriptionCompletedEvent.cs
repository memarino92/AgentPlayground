namespace PersonalAgent.Contracts.Messaging.Events;

public record CoachCallTranscriptionCompletedEvent(
    Guid UploadId,
    Guid SessionId,
    string ProfileId,
    Guid CorrelationId,
    int UtteranceCount,
    DateTimeOffset CompletedAtUtc);
