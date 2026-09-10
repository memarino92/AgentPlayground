namespace AgentPlayground.Contracts.Messaging.Requests;

public record TranscriptionRequest(Guid UploadId, string ProfileId);
