namespace AgentPlayground.Contracts.Events;

public record CoachCallStatusChangedEvent(Guid UploadId, string ProfileId, string Status);
