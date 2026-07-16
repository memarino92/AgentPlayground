namespace PersonalAgent.Worker.Models;

internal record CoachTranscriptProcessingResult(string SummaryMarkdown, string SummaryJson, List<ProcessedCoachChunk> Chunks);
