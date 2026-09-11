using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface ICoachEvidenceService
{
    Task<CoachEvidenceResponse?> GetAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken);
    Task<CoachAudio?> GetAudioAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken);
    Task<DeleteCoachAudioResult> DeleteAudioAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken);
}
