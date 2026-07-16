namespace AgentPlayground.Contracts.Events;

public record CoachCallProcessingCompletedEvent(
    Guid UploadId,
    Guid SessionId,
    string ProfileId,
    Guid CorrelationId,
    int ChunkCount,
    DateTimeOffset CompletedAtUtc);
