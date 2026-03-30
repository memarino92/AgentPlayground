namespace PersonalAgent.Models;

internal record SessionSummary(Guid SessionId, string Snippet, DateTimeOffset LastActivityAt, DateTimeOffset CreatedAt);
