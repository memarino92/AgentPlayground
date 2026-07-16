namespace PersonalAgent.Models;

internal record CoachCheckinChunk(
    int ChunkIndex,
    int StartMs,
    int EndMs,
    string Content,
    string SpeakerMix,
    string ExerciseTags,
    string IntentTags,
    string PriorityTags,
    string MetadataJson,
    float[] Embedding);
