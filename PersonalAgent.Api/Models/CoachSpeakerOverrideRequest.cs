namespace PersonalAgent.Api.Models;

internal record CoachSpeakerOverrideRequest
{
    public required string ProfileId { get; init; }
    public required List<CoachSpeakerOverrideItem> Overrides { get; init; }
}
