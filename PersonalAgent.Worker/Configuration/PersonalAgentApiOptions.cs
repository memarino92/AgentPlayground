namespace PersonalAgent.Worker.Configuration;

internal record PersonalAgentApiOptions
{
    public const string SectionName = "PersonalAgentApi";
    public string BaseUrl { get; set; } = "http://localhost:5100";
    public string InternalApiKey { get; set; } = string.Empty;
    public string ActorSigningKey { get; set; } = string.Empty;
}
