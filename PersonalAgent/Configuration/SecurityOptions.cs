namespace PersonalAgent.Configuration;

internal record SecurityOptions
{
    public string[] AllowedOrigins { get; set; } = [];
    public string InternalApiKey { get; set; } = string.Empty;
    public RateLimitOptions RateLimit { get; set; } = new();
}

internal record RateLimitOptions
{
    public int PermitLimit { get; set; } = 60;
    public int WindowSeconds { get; set; } = 60;
}
