namespace PersonalAgent.Models;

internal record SendMessageRequest(string ProfileId, string Message, string? ActorId = null, string Role = AgentRoles.Owner)
{
    public AgentAccessContext ToAccessContext() => new(ActorId ?? ProfileId, Role, ProfileId);
}
