namespace PersonalAgent.Services;

internal record PersistedAgentSession(
    Guid SessionId,
    string ProfileId,
    string SessionStateJson,
    long LastMessageSequence,
    string? ActorId = null,
    string Role = "Owner",
    string? MemoryProfileId = null)
{
    public string EffectiveActorId => ActorId ?? ProfileId;
    public string EffectiveMemoryProfileId => MemoryProfileId ?? ProfileId;
}
