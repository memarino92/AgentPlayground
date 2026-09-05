namespace PersonalAgent.Models;

internal record AgentAccessContext(string ActorId, string Role, string SubjectProfileId)
{
    public string MemoryProfileId => Role == AgentRoles.Owner ? SubjectProfileId : $"actor:{ActorId}";
}
