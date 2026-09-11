using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface ICoachEvidenceService
{
    Task<CoachEvidenceResponse?> GetAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken);
    Task<CoachAudio?> GetAudioAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken);
    Task<DeleteCoachAudioResult> DeleteAudioAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken);
    Task<AttachCoachAudioResult> AttachAudioAsync(Guid UploadId, string ProfileId, byte[] Bytes, string ContentType, CancellationToken CancellationToken);
}
