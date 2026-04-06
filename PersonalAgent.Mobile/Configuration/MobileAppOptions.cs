namespace PersonalAgent.Mobile.Configuration;

public record MobileAppOptions
{
    public string ApiBaseUrl { get; init; } = "https://localhost:5001";
    public string WebAppUrl { get; init; } = "https://localhost:5000";
    public string InternalApiKey { get; init; } = string.Empty;
    public string ProfileId { get; init; } = "mobile-dev";
    public string AndroidChannelId { get; init; } = "agent-approval-high";
}
