namespace PersonalAgent.Configuration;

internal record ChatModelCatalogOptions
{
    public const string SectionName = "ChatModels";

    public bool DiscoverFromProvider { get; init; } = true;
    public int RefreshIntervalSeconds { get; init; } = 300;
    public int FailureRetrySeconds { get; init; } = 30;
    public int DiscoveryTimeoutSeconds { get; init; } = 5;
    public List<ChatModelOption> Models { get; init; } = [];
}

internal record ChatModelOption
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}
