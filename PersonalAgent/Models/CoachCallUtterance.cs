namespace PersonalAgent.Models;

internal record CoachCallUtterance(int SpeakerLabel, string SpeakerRole, int StartMs, int EndMs, string Text, double Confidence);
