namespace PersonalAgent.Worker.Models;

internal record TranscribedUtterance(int SpeakerLabel, string SpeakerRole, int StartMs, int EndMs, string Text, double Confidence);
