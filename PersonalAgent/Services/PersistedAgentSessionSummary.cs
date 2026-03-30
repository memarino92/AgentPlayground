namespace PersonalAgent.Services;

internal record PersistedAgentSessionSummary(Guid SessionId, string Snippet, DateTimeOffset LastActivityAt, DateTimeOffset CreatedAt);
