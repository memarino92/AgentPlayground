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
                opts.Schema = Environment.GetEnvironmentVariable("AGENT_MEMORY_SCHEMA")
                    ?? configuration[$"{AgentMemoryOptions.SectionName}:Schema"]
                    ?? opts.Schema;

                var createInfrastructure = Environment.GetEnvironmentVariable("AGENT_MEMORY_CREATE_INFRASTRUCTURE")
                    ?? configuration[$"{AgentMemoryOptions.SectionName}:CreateInfrastructure"];

                if (bool.TryParse(createInfrastructure, out var shouldCreateInfrastructure))
                    opts.CreateInfrastructure = shouldCreateInfrastructure;

                var enableSemanticMemory = Environment.GetEnvironmentVariable("AGENT_MEMORY_ENABLE_SEMANTIC_MEMORY")
                    ?? configuration[$"{AgentMemoryOptions.SectionName}:EnableSemanticMemory"];

                if (bool.TryParse(enableSemanticMemory, out var shouldEnableSemanticMemory))
                    opts.EnableSemanticMemory = shouldEnableSemanticMemory;

                opts.EmbeddingModel = Environment.GetEnvironmentVariable("AGENT_MEMORY_EMBEDDING_MODEL")
                    ?? configuration[$"{AgentMemoryOptions.SectionName}:EmbeddingModel"]
                    ?? opts.EmbeddingModel;

                var vectorDimensions = Environment.GetEnvironmentVariable("AGENT_MEMORY_VECTOR_DIMENSIONS")
                    ?? configuration[$"{AgentMemoryOptions.SectionName}:VectorDimensions"];

                if (int.TryParse(vectorDimensions, out var parsedVectorDimensions) && parsedVectorDimensions > 0)
                    opts.VectorDimensions = parsedVectorDimensions;
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
        var connectionString = Environment.GetEnvironmentVariable("AGENT_MEMORY_CONNECTION_STRING")
            ?? configuration[$"{AgentMemoryOptions.SectionName}:ConnectionString"]
            ?? configuredValue;

        return PostgresConnectionStringNormalizer.Normalize(connectionString)
            ?? PostgresConnectionStringNormalizer.Normalize(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? PostgresConnectionStringNormalizer.Normalize(messagingConnectionString)
            ?? string.Empty;
    }
}
