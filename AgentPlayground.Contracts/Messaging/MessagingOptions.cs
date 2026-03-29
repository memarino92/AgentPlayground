namespace AgentPlayground.Contracts.Messaging;

public record MessagingOptions
{
    public const string SectionName = "Messaging";

    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "transport";
    public bool CreateInfrastructure { get; set; } = true;
}
