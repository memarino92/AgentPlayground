using PersonalAgent.Worker.Models;

namespace PersonalAgent.Worker.Services;

internal interface ITranscriptionService
{
    Task<List<TranscribedUtterance>> TranscribeAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default);
}
