using PersonalAgent.Models;

namespace PersonalAgent.Security;

internal record SignedActor(string ActorId, string Role, string? Email)
{
    public AgentAccessContext ForSubject(string subjectProfileId) => new(ActorId, Role, subjectProfileId);
}
