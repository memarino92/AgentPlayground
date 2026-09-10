using System.Security.Cryptography;

using AgentPlayground.Contracts.Messaging.Responses;

using PersonalAgent.Services;

namespace PersonalAgent.Development;

internal sealed class SyntheticTranscriptionProvider : ITranscriptionProvider
{
    public Task<string> SubmitAsync(byte[] Audio, string MimeType, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult("synthetic-" + Convert.ToHexString(SHA256.HashData(Audio)));
    }

    public Task<TranscriptionResponse> GetResultAsync(Guid JobId, string ProviderJobId, CancellationToken CancellationToken)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TranscriptionResponse(JobId, TranscriptionStatus.Completed,
        [
            new(0, 0, 4000, "How can I improve my deadlift setup?", 1),
            new(1, 4000, 9000, "Brace before the pull and keep the bar close to your shins.", 1)
        ]));
    }
}
