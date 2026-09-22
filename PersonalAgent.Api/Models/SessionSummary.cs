namespace PersonalAgent.Api.Models;

internal record SessionSummary(Guid SessionId, string Snippet, DateTimeOffset LastActivityAt, DateTimeOffset CreatedAt);
