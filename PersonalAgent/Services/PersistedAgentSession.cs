namespace PersonalAgent.Services;

internal record PersistedAgentSession(Guid SessionId, string ProfileId, string SessionStateJson, long LastMessageSequence);
