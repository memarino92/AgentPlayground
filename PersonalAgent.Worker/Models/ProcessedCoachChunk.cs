namespace PersonalAgent.Worker.Models;

internal record ProcessedCoachChunk(
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
