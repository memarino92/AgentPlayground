namespace PersonalAgent.Models;

internal record CoachCallUploadResult(Guid UploadId, Guid CorrelationId, CoachCallUploadStatus Status, DateTimeOffset CreatedAtUtc, bool IsDuplicate);
