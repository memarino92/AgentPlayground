using PersonalAgent.Contracts.Messaging.Requests;
using PersonalAgent.Contracts.Messaging.Responses;
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
        // The API summary step runs after chunk persistence and owns the executive summary.
        return new CoachTranscriptProcessingResult("", "{}", enrichedChunks);
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
