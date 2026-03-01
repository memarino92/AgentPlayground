namespace PersonalAgent.Configuration;

internal record SecurityOptions
{
    public string[] AllowedOrigins { get; init; } = [];
    public string InternalApiKey { get; init; } = string.Empty;
    public RateLimitOptions RateLimit { get; init; } = new();
}

internal record RateLimitOptions
{
    public int PermitLimit { get; init; } = 60;
    public int WindowSeconds { get; init; } = 60;
}
