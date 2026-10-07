namespace PersonalAgent.Mobile.Configuration;

public record MobileAppOptions
{
    public string ApiBaseUrl { get; init; } = "https://pa-api.michaelmarino.dev";
    public string WebAppUrl { get; init; } = "https://pa.michaelmarino.dev";
    public string InternalApiKey { get; init; } = string.Empty;
    public string ProfileId { get; init; } = "mobile-dev";
    public string AndroidChannelId { get; init; } = "agent-approval-high";
}
