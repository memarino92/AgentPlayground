namespace PersonalAgent.Api.Models;

internal record SessionSummaryPage(IReadOnlyList<SessionSummary> Sessions, DateTimeOffset? NextBeforeActivityAt, Guid? NextBeforeSessionId, bool HasMore);
