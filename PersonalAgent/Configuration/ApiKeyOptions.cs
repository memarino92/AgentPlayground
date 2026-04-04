namespace PersonalAgent.Configuration;

internal record ApiKeyOptions
{
    public string OpenAiKey { get; set; } = string.Empty;
    public string InternalApiKey { get; set; } = string.Empty;
    public string TavilyApiKey { get; set; } = string.Empty;
    public string TavilyMcpUrl { get; set; } = "https://mcp.tavily.com/mcp";
    public string TavilyDefaultParameters { get; set; } = string.Empty;
    public bool EnableWebSearch { get; set; } = true;
}
