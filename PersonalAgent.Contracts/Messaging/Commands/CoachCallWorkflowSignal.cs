namespace PersonalAgent.Contracts.Messaging.Commands;

public record CoachCallWorkflowSignal(Guid UploadId, Guid SessionId, string ProfileId, string Stage);
