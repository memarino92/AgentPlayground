namespace PersonalAgent.Contracts.Messaging.Commands;

public record TranscribeCoachCallCommand(Guid UploadId, string ProfileId, Guid CorrelationId);
