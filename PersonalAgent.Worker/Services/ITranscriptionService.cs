using PersonalAgent.Worker.Models;

namespace PersonalAgent.Worker.Services;

internal interface ITranscriptionService
{
    Task<List<TranscribedUtterance>> TranscribeAsync(byte[] audioBytes, string fileName, string mimeType, CancellationToken cancellationToken = default);
}
