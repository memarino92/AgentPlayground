using PersonalAgent.Contracts.Messaging.Responses;

namespace PersonalAgent.Api.Services;

internal interface ITranscriptionProvider
{
    Task<string> SubmitAsync(byte[] Audio, string MimeType, CancellationToken CancellationToken);
    Task<TranscriptionResponse> GetResultAsync(Guid JobId, string ProviderJobId, CancellationToken CancellationToken);
}
