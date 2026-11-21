using Microsoft.Extensions.Options;

using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal interface ICoachUploadStore
{
    Task<CoachCallUploadResult> CreateUploadAsync(string profileId, string originalFileName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default);
    Task<CoachCheckinStatusResponse?> GetStatusAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default);
}

// Both chat and the manual upload route use this boundary and the existing transactional outbox.
internal sealed class CoachUploadActions(ICoachUploadStore Store, IOptions<CoachCheckinOptions> Options)
{
    private static void Authorize(AgentAccessContext Access)
    {
        if (Access.Role != AgentRoles.Owner || Access.ScheduledTaskId is not null
            || !string.Equals(Access.ActorId, Access.SubjectProfileId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the signed-in owner may submit their own coaching audio.");
    }

    public Task<CoachCallUploadResult> UploadAsync(AgentAccessContext Access, string Name, string MimeType, byte[] Bytes, CancellationToken Token)
    {
        Authorize(Access);
        if (!Name.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) || Bytes.Length == 0
            || Bytes.LongLength > Options.Value.MaxUploadMb * 1024L * 1024L)
            throw new ArgumentException($"Choose a non-empty .m4a file up to {Options.Value.MaxUploadMb}MB.");
        return Store.CreateUploadAsync(Access.SubjectProfileId, Path.GetFileName(Name), MimeType, Bytes, Token);
    }

    public Task<CoachCheckinStatusResponse?> StatusAsync(AgentAccessContext Access, Guid UploadId, CancellationToken Token)
    {
        Authorize(Access);
        return Store.GetStatusAsync(UploadId, Access.SubjectProfileId, Token);
    }
}
