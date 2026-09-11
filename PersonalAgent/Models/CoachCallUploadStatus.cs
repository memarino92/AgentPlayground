namespace PersonalAgent.Models;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CoachCallUploadStatus>))]
internal enum CoachCallUploadStatus
{
    Uploaded,
    Transcribing,
    AwaitingSpeakerOverride,
    Processing,
    Completed,
    Failed
}
