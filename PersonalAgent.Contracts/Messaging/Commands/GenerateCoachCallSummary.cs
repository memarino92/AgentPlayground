namespace PersonalAgent.Contracts.Messaging.Commands;

public record GenerateCoachCallSummary(Guid UploadId, Guid SessionId, string ProfileId);
