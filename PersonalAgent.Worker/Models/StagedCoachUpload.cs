namespace PersonalAgent.Worker.Models;

internal record StagedCoachUpload(Guid UploadId, Guid SessionId, string ProfileId, Guid CorrelationId);
