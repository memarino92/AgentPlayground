namespace PersonalAgent.Models;

internal record CoachCheckinTranscriptResponse(
    Guid UploadId,
    Guid SessionId,
    string ProfileId,
    CoachCallUploadStatus Status,
    string TranscriptText,
    DateTimeOffset UpdatedAtUtc,
    List<CoachCheckinTranscriptUtterance> Utterances);

internal record CoachCheckinTranscriptUtterance(
    int SpeakerLabel,
    string SpeakerRole,
    int StartMs,
    int EndMs,
    string Text,
    double Confidence);
