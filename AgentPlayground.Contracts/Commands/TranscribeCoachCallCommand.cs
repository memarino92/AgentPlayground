namespace AgentPlayground.Contracts.Commands;

public record TranscribeCoachCallCommand(Guid UploadId, string ProfileId, Guid CorrelationId);
