namespace PersonalAgent.Services;

internal record PersistedAgentSession(Guid SessionId, string SessionStateJson, long LastMessageSequence);
