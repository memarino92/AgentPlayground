namespace PersonalAgent.Models;

internal record CoachProfileAssignment(
    string CoachEmail,
    string? CoachActorId,
    string SubjectProfileId,
    bool IsActive,
    DateTimeOffset UpdatedAt);

internal record SaveCoachProfileAssignmentRequest(
    string CoachEmail,
    string SubjectProfileId,
    bool IsActive,
    string UpdatedBy);
