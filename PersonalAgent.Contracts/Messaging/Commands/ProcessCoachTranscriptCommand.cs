namespace PersonalAgent.Contracts.Messaging.Commands;

public record ProcessCoachTranscriptCommand(Guid UploadId, Guid SessionId, string ProfileId, Guid CorrelationId);
