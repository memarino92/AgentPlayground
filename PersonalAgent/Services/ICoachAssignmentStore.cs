using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface ICoachAssignmentStore
{
    Task<IReadOnlyList<string>> GetAssignedProfilesAsync(string coachActorId, string coachEmail, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CoachProfileAssignment>> GetAssignmentsAsync(string subjectProfileId, CancellationToken cancellationToken = default);
    Task SaveAssignmentAsync(SaveCoachProfileAssignmentRequest request, CancellationToken cancellationToken = default);
}
