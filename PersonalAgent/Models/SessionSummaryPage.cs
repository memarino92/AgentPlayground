namespace PersonalAgent.Models;

internal record SessionSummaryPage(IReadOnlyList<SessionSummary> Sessions, DateTimeOffset? NextBeforeActivityAt, Guid? NextBeforeSessionId, bool HasMore);
