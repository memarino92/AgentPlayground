namespace PersonalAgent.Models;

internal record CoachCheckinSummaryResponse(
    Guid UploadId,
    Guid SessionId,
    string SummaryMarkdown,
    string SummaryJson,
    DateTimeOffset UpdatedAtUtc);
