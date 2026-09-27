namespace PersonalAgent.Contracts.Messaging.Commands;

public record StartCoachCallWorkflow(Guid UploadId, Guid SessionId, string ProfileId, Guid CorrelationId, string Status = "Uploaded");
