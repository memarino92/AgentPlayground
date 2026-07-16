namespace PersonalAgent.Models;

internal record CoachCheckinStatusResponse(
    Guid UploadId,
    Guid SessionId,
    string ProfileId,
    CoachCallUploadStatus Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
