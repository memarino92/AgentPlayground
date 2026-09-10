using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using AgentPlayground.Contracts.Messaging.Responses;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.Services;

internal class AssemblyAiTranscriptionService(IHttpClientFactory httpClientFactory, IOptions<AssemblyAiOptions> options) : ITranscriptionProvider
{
    private readonly AssemblyAiOptions _options = options.Value;

    public async Task<string> SubmitAsync(byte[] Audio, string MimeType, CancellationToken CancellationToken)
    {
        var Client = httpClientFactory.CreateClient("AssemblyAi");
        var UploadUrl = await UploadAudioAsync(Client, Audio, MimeType, CancellationToken);
        return await SubmitTranscriptionAsync(Client, UploadUrl, CancellationToken);
    }

    private async Task<string> UploadAudioAsync(HttpClient client, byte[] audioBytes, string mimeType, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(audioBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mimeType) ? "audio/m4a" : mimeType);
        using var response = await client.PostAsync("upload", content, cancellationToken);
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
        using var response = await client.PostAsync("transcript", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("id", out var idElement))
            throw new InvalidOperationException("AssemblyAI transcript response did not include id.");
        return idElement.GetString() ?? throw new InvalidOperationException("AssemblyAI transcript id was null.");
    }

    public async Task<TranscriptionResponse> GetResultAsync(Guid JobId, string ProviderJobId, CancellationToken CancellationToken)
    {
        var Client = httpClientFactory.CreateClient("AssemblyAi");
        using var Response = await Client.GetAsync($"transcript/{Uri.EscapeDataString(ProviderJobId)}", CancellationToken);
        Response.EnsureSuccessStatusCode();
        using var Document = JsonDocument.Parse(await Response.Content.ReadAsStringAsync(CancellationToken));
        return Document.RootElement.GetProperty("status").GetString() switch
        {
            "completed" => new(JobId, TranscriptionStatus.Completed, ParseUtterances(Document.RootElement)),
            "error" => new(JobId, TranscriptionStatus.Failed, [], "Transcription failed."),
            "queued" or "processing" => new(JobId, TranscriptionStatus.Pending, [], RetryAfterSeconds: _options.PollIntervalSeconds),
            _ => throw new InvalidOperationException("Unrecognized transcription status.")
        };
    }

    private static List<TranscriptSegment> ParseUtterances(JsonElement root)
    {
        if (!root.TryGetProperty("utterances", out var utterancesElement) || utterancesElement.ValueKind != JsonValueKind.Array)
            return [];

        var utterances = new List<TranscriptSegment>();
        foreach (var element in utterancesElement.EnumerateArray())
        {
            var speakerLabel = ParseSpeaker(element.TryGetProperty("speaker", out var speakerElement) ? speakerElement.GetString() : null);
            var startMs = element.TryGetProperty("start", out var startElement) ? startElement.GetInt32() : 0;
            var endMs = element.TryGetProperty("end", out var endElement) ? endElement.GetInt32() : startMs;
            var text = element.TryGetProperty("text", out var textElement) ? textElement.GetString() ?? string.Empty : string.Empty;
            var confidence = element.TryGetProperty("confidence", out var confidenceElement) ? confidenceElement.GetDouble() : 0.0;
            utterances.Add(new TranscriptSegment(speakerLabel, startMs, endMs, text, confidence));
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
