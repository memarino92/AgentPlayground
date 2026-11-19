namespace PersonalAgent.Presentation;

public record CoachCheckinTranscriptUtteranceResponse(int SpeakerLabel, string SpeakerRole, int StartMs, int EndMs, string Text, double Confidence, string SpeakerName = "");
public record CoachCheckinSpeakerLabelInfoResponse(int SpeakerLabel, string SpeakerRole, int UtteranceCount, List<string> SampleTexts);
public record CoachCheckinAdminItemResponse(Guid UploadId, Guid SessionId, string ProfileId, string OriginalFileName, string Status, string? Error, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool HasAudioBlob, int UtteranceCount, int ChunkCount, List<CoachCheckinSpeakerLabelInfoResponse> SpeakerLabels, string? ExecutiveSummary = null);
