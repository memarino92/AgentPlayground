namespace PersonalAgent.Configuration;

internal record ApiKeyOptions
{
    public string OpenAiKey { get; set; } = string.Empty;
    public string InternalApiKey { get; set; } = string.Empty;
}
