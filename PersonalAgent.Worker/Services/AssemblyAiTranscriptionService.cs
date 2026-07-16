using Microsoft.Extensions.Options;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.Worker.Services;

internal class AssemblyAiTranscriptionService(IHttpClientFactory httpClientFactory, IOptions<AssemblyAiOptions> options) : ITranscriptionService
{
    private readonly AssemblyAiOptions _options = options.Value;

    public async Task<List<TranscribedUtterance>> TranscribeAsync(byte[] audioBytes, string fileName, string mimeType, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient("AssemblyAi");

        var uploadUrl = await UploadAudioAsync(client, audioBytes, mimeType, cancellationToken);
        var transcriptId = await SubmitTranscriptionAsync(client, uploadUrl, cancellationToken);
        return await PollResultAsync(client, transcriptId, cancellationToken);
    }

    private async Task<string> UploadAudioAsync(HttpClient client, byte[] audioBytes, string mimeType, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(audioBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "audio/m4a" : mimeType);
        var response = await client.PostAsync("upload", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("upload_url", out var uploadUrlElement))
            throw new InvalidOperationException("AssemblyAI upload response did not include upload_url.");
        return uploadUrlElement.GetString() ?? throw new InvalidOperationException("AssemblyAI upload_url was null.");
    }

    private async Task<string> SubmitTranscriptionAsync(HttpClient client, string uploadUrl, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new
        {
            audio_url = uploadUrl,
            speech_models = _options.SpeechModels,
            speaker_labels = true
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("transcript", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("AssemblyAI transcript response did not include id.");
        return idElement.GetString() ?? throw new InvalidOperationException("AssemblyAI transcript id was null.");
    }

    private async Task<List<TranscribedUtterance>> PollResultAsync(HttpClient client, string transcriptId, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(_options.TranscriptionTimeoutMinutes);
        var startedAt = DateTimeOffset.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await client.GetAsync($"transcript/{transcriptId}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(payload);
            var status = document.RootElement.GetProperty("status").GetString();

            switch (status)
            {
                case "completed":
                    return ParseUtterances(document.RootElement);
                case "error":
                {
                    var error = document.RootElement.TryGetProperty("error", out var errorElement)
                        ? errorElement.GetString()
                        : "Unknown transcription error";
                    throw new InvalidOperationException($"AssemblyAI transcription failed: {error}");
                }
            }

            if (DateTimeOffset.UtcNow - startedAt > timeout)
                throw new TimeoutException($"AssemblyAI transcription timed out after {timeout.TotalMinutes} minutes.");

            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cancellationToken);
        }
    }

    private static List<TranscribedUtterance> ParseUtterances(JsonElement root)
    {
        if (!root.TryGetProperty("utterances", out var utterancesElement) || utterancesElement.ValueKind != JsonValueKind.Array)
            return [];

        var utterances = new List<TranscribedUtterance>();
        foreach (var element in utterancesElement.EnumerateArray())
        {
            var speakerLabel = ParseSpeaker(element.TryGetProperty("speaker", out var speakerElement) ? speakerElement.GetString() : null);
            var startMs = element.TryGetProperty("start", out var startElement) ? startElement.GetInt32() : 0;
            var endMs = element.TryGetProperty("end", out var endElement) ? endElement.GetInt32() : startMs;
            var text = element.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
            var confidence = element.TryGetProperty("confidence", out var confidenceElement) ? confidenceElement.GetDouble() : 0.0;
            utterances.Add(new TranscribedUtterance(speakerLabel, "unknown", startMs, endMs, text, confidence));
        }

        return utterances;
    }

    private static int ParseSpeaker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (int.TryParse(value, out var numeric)) return numeric;
        return value[0] - 'A';
    }
}
