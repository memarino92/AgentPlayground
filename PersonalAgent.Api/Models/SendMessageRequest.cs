namespace PersonalAgent.Api.Models;

internal record SendMessageRequest(string ProfileId, string Message, string? ActorId = null, string Role = AgentRoles.Owner)
{
    public Guid? RunId { get; init; }
    public bool ObserveOnly { get; init; }
    public AgentAccessContext ToAccessContext() => new(ActorId ?? ProfileId, Role, ProfileId);
}
