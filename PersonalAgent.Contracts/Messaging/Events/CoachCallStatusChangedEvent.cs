namespace PersonalAgent.Contracts.Messaging.Events;

public record CoachCallStatusChangedEvent(Guid UploadId, string ProfileId, string Status);
