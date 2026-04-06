namespace PersonalAgent.Configuration;

internal record AgentMemoryOptions
{
    public const string SectionName = "AgentMemory";

    public string ConnectionString { get; set; } = string.Empty;
    public string Schema { get; set; } = "agent_memory";
    public bool CreateInfrastructure { get; set; } = true;
    public bool EnableSemanticMemory { get; set; }
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public int VectorDimensions { get; set; } = 1536;
    public string SessionsTableName { get; set; } = "sessions";
    public string TranscriptMessagesTableName { get; set; } = "transcript_messages";
    public string MemoryRecordsTableName { get; set; } = "memory_records";
    public string MobileDeviceTokensTableName { get; set; } = "mobile_device_tokens";
    public string AgentApprovalsTableName { get; set; } = "agent_approvals";
}
