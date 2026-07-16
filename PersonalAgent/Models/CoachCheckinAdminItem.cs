namespace PersonalAgent.Models;

internal record CoachCheckinAdminItem(
    Guid UploadId,
    Guid SessionId,
    string ProfileId,
    string OriginalFileName,
    CoachCallUploadStatus Status,
    string? Error,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool HasAudioBlob,
    int UtteranceCount,
    int ChunkCount);
