using AgentPlayground.Contracts.Messaging.Responses;

namespace PersonalAgent.Services;

internal interface ITranscriptionProvider
{
    Task<string> SubmitAsync(byte[] Audio, string MimeType, CancellationToken CancellationToken);
    Task<TranscriptionResponse> GetResultAsync(Guid JobId, string ProviderJobId, CancellationToken CancellationToken);
}
