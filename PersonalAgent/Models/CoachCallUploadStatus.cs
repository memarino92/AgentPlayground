namespace PersonalAgent.Models;

internal enum CoachCallUploadStatus
{
    Uploaded,
    Transcribing,
    AwaitingSpeakerOverride,
    Processing,
    Completed,
    Failed
}
