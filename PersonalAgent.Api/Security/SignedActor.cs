using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Security;

internal record SignedActor(string ActorId, string Role, string? Email)
{
    public AgentAccessContext ForSubject(string subjectProfileId) => new(ActorId, Role, subjectProfileId) { Email = Email };
}
