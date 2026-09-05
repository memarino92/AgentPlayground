namespace PersonalAgent.Models;

internal record CreateSessionRequest(string ProfileId, string? ModelId, string? ActorId = null, string Role = AgentRoles.Owner)
{
    public AgentAccessContext ToAccessContext() => new(ActorId ?? ProfileId, Role, ProfileId);
}
