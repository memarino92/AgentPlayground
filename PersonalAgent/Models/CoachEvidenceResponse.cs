namespace PersonalAgent.Models;

/// <summary>Original call transcript and current recording availability.</summary>
internal sealed record CoachEvidenceResponse(CoachCheckinTranscriptResponse Transcript, bool AudioAvailable);

internal sealed record CoachAudio(byte[] Bytes, string ContentType);

internal enum DeleteCoachAudioResult { Deleted, NotFound, Pending }
