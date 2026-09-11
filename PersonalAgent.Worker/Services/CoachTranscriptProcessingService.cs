using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using MassTransit;
using PersonalAgent.Worker.Models;
using System.Text.Json;

namespace PersonalAgent.Worker.Services;

internal class CoachTranscriptProcessingService(IRequestClient<GenerateEmbeddingsRequest> embeddingRequestClient)
{
    public async Task<CoachTranscriptProcessingResult> ProcessAsync(Guid correlationId, IReadOnlyList<TranscribedUtterance> utterances, CancellationToken cancellationToken = default)
    {
        if (utterances.Count is 0) return new CoachTranscriptProcessingResult("No transcript content available.", "{}", []);

        var chunks = BuildChunks(utterances);
        var embeddings = await GenerateEmbeddingsAsync(correlationId, chunks.Select(chunk => chunk.Content).ToList(), cancellationToken);
        if (embeddings.Count != chunks.Count)
            throw new InvalidOperationException($"Embedding count mismatch. Expected {chunks.Count}, received {embeddings.Count}.");

        var enrichedChunks = chunks.Select((chunk, index) => chunk with
        {
            MetadataJson = MergeMetadata(chunk.MetadataJson, embeddings[index].Length),
            Embedding = embeddings[index]
        }).ToList();
        var summaryMarkdown = BuildSummaryMarkdown(enrichedChunks);
        var summaryJson = BuildSummaryJson(enrichedChunks);
        return new CoachTranscriptProcessingResult(summaryMarkdown, summaryJson, enrichedChunks);
    }

    private async Task<List<float[]>> GenerateEmbeddingsAsync(Guid correlationId, List<string> inputs, CancellationToken cancellationToken)
    {
        var response = await embeddingRequestClient.GetResponse<GenerateEmbeddingsResponse>(
            new GenerateEmbeddingsRequest(correlationId, "PersonalAgent.Worker", inputs),
            cancellationToken);
        return response.Message.Embeddings;
    }

    private static List<ProcessedCoachChunk> BuildChunks(IReadOnlyList<TranscribedUtterance> utterances)
    {
        var chunks = new List<ProcessedCoachChunk>();
        var window = new List<TranscribedUtterance>();
        foreach (var utterance in utterances)
        {
            window.Add(utterance);
            if (window.Count < 4) continue;
            chunks.Add(CreateChunk(chunks.Count, window));
            window.Clear();
        }

        if (window.Count > 0)
            chunks.Add(CreateChunk(chunks.Count, window));
        return chunks;
    }

    private static ProcessedCoachChunk CreateChunk(int index, IReadOnlyList<TranscribedUtterance> utterances)
    {
        var content = string.Join("\n", utterances.Select(utterance => $"{utterance.SpeakerRole}: {utterance.Text}"));
        var startMs = utterances.Min(utterance => utterance.StartMs);
        var endMs = utterances.Max(utterance => utterance.EndMs);
        var speakerRoles = utterances.Select(utterance => utterance.SpeakerRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var speakerMix = speakerRoles.Count switch
        {
            0 => "unknown",
            1 when speakerRoles[0].Equals("coach", StringComparison.OrdinalIgnoreCase) => "coach_only",
            1 when speakerRoles[0].Equals("athlete", StringComparison.OrdinalIgnoreCase) => "athlete_only",
            1 => "single_speaker",
            _ => "mixed"
        };

        var lower = content.ToLowerInvariant();
        var exerciseTags = BuildTagArray(lower, ["squat", "bench", "deadlift", "press", "row", "pull", "conditioning", "yoke"]);
        var intentTags = BuildTagArray(lower, ["cue", "technique", "programming", "recovery", "pain", "nutrition", "mindset"]);
        var priorityTags = BuildTagArray(lower, ["action", "warning", "goal", "followup"]);

        var metadata = JsonSerializer.Serialize(new
        {
            chunkIndex = index,
            utteranceCount = utterances.Count,
            avgConfidence = utterances.Average(utterance => utterance.Confidence),
            speakerLabels = utterances.Select(utterance => utterance.SpeakerLabel).Distinct().OrderBy(value => value).ToArray()
        });

        return new ProcessedCoachChunk(index, startMs, endMs, content, speakerMix, exerciseTags, intentTags, priorityTags, metadata, []);
    }

    private static string BuildTagArray(string content, IReadOnlyList<string> candidates)
    {
        var tags = candidates.Where(content.Contains).Distinct().ToList();
        return JsonSerializer.Serialize(tags);
    }

    private static string BuildSummaryMarkdown(IReadOnlyList<ProcessedCoachChunk> chunks)
    {
        var lines = new List<string>
        {
            "# Coach Check-In Summary",
            $"- Chunks: {chunks.Count}",
            $"- Exercises discussed: {string.Join(", ", ExtractTags(chunks.Select(chunk => chunk.ExerciseTags)).DefaultIfEmpty("none"))}",
            $"- Main intents: {string.Join(", ", ExtractTags(chunks.Select(chunk => chunk.IntentTags)).DefaultIfEmpty("none"))}",
            "",
            "## Key Cues"
        };

        foreach (var chunk in chunks.Take(5))
            lines.Add($"- ({FormatTimestamp(chunk.StartMs)}-{FormatTimestamp(chunk.EndMs)}) {TrimLine(chunk.Content)}");

        return string.Join("\n", lines);
    }

    private static string BuildSummaryJson(IReadOnlyList<ProcessedCoachChunk> chunks)
    {
        var payload = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            exercises = ExtractTags(chunks.Select(chunk => chunk.ExerciseTags)),
            intents = ExtractTags(chunks.Select(chunk => chunk.IntentTags)),
            priorities = ExtractTags(chunks.Select(chunk => chunk.PriorityTags)),
            chunkCount = chunks.Count
        };
        return JsonSerializer.Serialize(payload);
    }

    private static IReadOnlyList<string> ExtractTags(IEnumerable<string> tagJson)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in tagJson)
        {
            try
            {
                var tags = JsonSerializer.Deserialize<List<string>>(json) ?? [];
                foreach (var tag in tags)
                    values.Add(tag);
            }
            catch
            {
            }
        }

        return values.OrderBy(value => value).ToList();
    }

    private static string TrimLine(string content)
    {
        var firstLine = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? content;
        return firstLine.Length <= 120 ? firstLine : firstLine[..117] + "...";
    }

    private static string FormatTimestamp(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss");

    private static string MergeMetadata(string metadataJson, int embeddingDimensions)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var root = document.RootElement;
        var payload = new Dictionary<string, object?>
        {
            ["chunkIndex"] = root.TryGetProperty("chunkIndex", out var chunkIndex) ? chunkIndex.GetInt32() : 0,
            ["utteranceCount"] = root.TryGetProperty("utteranceCount", out var utteranceCount) ? utteranceCount.GetInt32() : 0,
            ["avgConfidence"] = root.TryGetProperty("avgConfidence", out var avgConfidence) ? avgConfidence.GetDouble() : 0,
            ["embeddingDimensions"] = embeddingDimensions
        };
        return JsonSerializer.Serialize(payload);
    }
}
