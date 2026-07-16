namespace PersonalAgent.Models;

internal record CoachCheckinProcessResult(string SummaryMarkdown, string SummaryJson, List<CoachCheckinChunk> Chunks);
