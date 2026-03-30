namespace PersonalAgent.Configuration;

internal record ChatModelCatalogOptions
{
    public const string SectionName = "ChatModels";

    public List<ChatModelOption> Models { get; init; } = [];
}

internal record ChatModelOption
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
}
