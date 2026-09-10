namespace AgentPlayground.Contracts.Messaging.Responses;

public enum TranscriptionStatus { Pending, Completed, Failed }

public record TranscriptSegment(int SpeakerLabel, int StartMs, int EndMs, string Text, double Confidence);

public record TranscriptionResponse(Guid JobId, TranscriptionStatus Status, List<TranscriptSegment> Segments, string? Error = null, int RetryAfterSeconds = 4);
