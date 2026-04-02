using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Contracts.Messaging;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Configuration;

internal static class AgentMemoryOptionsServiceCollectionExtensions
{
    public static IServiceCollection AddAgentMemoryOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AgentMemoryOptions>()
            .Configure<IOptions<MessagingOptions>>((opts, messagingOptions) =>
            {
                configuration.GetSection(AgentMemoryOptions.SectionName).Bind(opts);

                opts.ConnectionString = ResolveConnectionString(configuration, opts.ConnectionString, messagingOptions.Value.ConnectionString);
                opts.Schema = ConfigurationValueResolver.ResolveString(configuration, "AGENT_MEMORY_SCHEMA", $"{AgentMemoryOptions.SectionName}:Schema", opts.Schema) ?? opts.Schema;
                opts.CreateInfrastructure = ConfigurationValueResolver.ResolveBool(
                    configuration,
                    "AGENT_MEMORY_CREATE_INFRASTRUCTURE",
                    $"{AgentMemoryOptions.SectionName}:CreateInfrastructure",
                    opts.CreateInfrastructure);
                opts.EnableSemanticMemory = ConfigurationValueResolver.ResolveBool(
                    configuration,
                    "AGENT_MEMORY_ENABLE_SEMANTIC_MEMORY",
                    $"{AgentMemoryOptions.SectionName}:EnableSemanticMemory",
                    opts.EnableSemanticMemory);
                opts.EmbeddingModel = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "AGENT_MEMORY_EMBEDDING_MODEL",
                    $"{AgentMemoryOptions.SectionName}:EmbeddingModel",
                    opts.EmbeddingModel) ?? opts.EmbeddingModel;
                opts.VectorDimensions = ConfigurationValueResolver.ResolveInt(
                    configuration,
                    "AGENT_MEMORY_VECTOR_DIMENSIONS",
                    $"{AgentMemoryOptions.SectionName}:VectorDimensions",
                    opts.VectorDimensions,
                    value => value > 0);
            })
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), $"{AgentMemoryOptions.SectionName}:ConnectionString is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.Schema), $"{AgentMemoryOptions.SectionName}:Schema is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.SessionsTableName), $"{AgentMemoryOptions.SectionName}:SessionsTableName is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.TranscriptMessagesTableName), $"{AgentMemoryOptions.SectionName}:TranscriptMessagesTableName is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.MemoryRecordsTableName), $"{AgentMemoryOptions.SectionName}:MemoryRecordsTableName is required")
            .Validate(opts => !opts.EnableSemanticMemory || opts.VectorDimensions > 0, $"{AgentMemoryOptions.SectionName}:VectorDimensions must be greater than zero when semantic memory is enabled")
            .ValidateOnStart();

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration, string configuredValue, string messagingConnectionString)
    {
        var connectionString = ConfigurationValueResolver.ResolveString(
            configuration,
            "AGENT_MEMORY_CONNECTION_STRING",
            $"{AgentMemoryOptions.SectionName}:ConnectionString",
            configuredValue);

        return PostgresConnectionStringNormalizer.Normalize(connectionString)
            ?? PostgresConnectionStringNormalizer.Normalize(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? PostgresConnectionStringNormalizer.Normalize(messagingConnectionString)
            ?? string.Empty;
    }
}
