namespace PersonalAgent.Mobile.Configuration;

public record MobileAppOptions
{
    public string ApiBaseUrl { get; init; } = "http://127.0.0.1:5100";
    public string WebAppUrl { get; init; } = "http://127.0.0.1:5100";
    public string InternalApiKey { get; init; } = string.Empty;
    public string ProfileId { get; init; } = "mobile-dev";
    public string AndroidChannelId { get; init; } = "agent-approval-high";
}
