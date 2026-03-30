using AgentPlayground.Contracts.Messaging;
using Microsoft.Extensions.Options;
using System.Text;

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

        return NormalizeConnectionString(connectionString)
            ?? NormalizeConnectionString(Environment.GetEnvironmentVariable("DATABASE_URL"))
            ?? NormalizeConnectionString(messagingConnectionString)
            ?? string.Empty;
    }

    private static string? NormalizeConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out var uri)) return connectionString;
        if (uri.Scheme is not "postgres" and not "postgresql") return connectionString;

        var userInfo = uri.UserInfo.Split(':', 2, StringSplitOptions.TrimEntries);
        var builder = new StringBuilder();

        AppendSetting(builder, "Host", uri.Host);
        AppendSetting(builder, "Port", uri.IsDefaultPort ? "5432" : uri.Port.ToString());
        AppendSetting(builder, "Database", uri.AbsolutePath.Trim('/'));
        AppendSetting(builder, "Username", userInfo.ElementAtOrDefault(0) is { Length: > 0 } username ? Uri.UnescapeDataString(username) : null);
        AppendSetting(builder, "Password", userInfo.ElementAtOrDefault(1) is { Length: > 0 } password ? Uri.UnescapeDataString(password) : null);

        foreach (var queryParameter in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = queryParameter.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length is 0 || string.IsNullOrWhiteSpace(parts[0])) continue;

            AppendSetting(builder, MapConnectionStringKey(parts[0]), parts.Length is 2 ? Uri.UnescapeDataString(parts[1]) : null);
        }

        return builder.ToString();
    }

    private static string MapConnectionStringKey(string key) => key.ToLowerInvariant() switch
    {
        "sslmode" => "SSL Mode",
        "trustservercertificate" => "Trust Server Certificate",
        "trust_server_certificate" => "Trust Server Certificate",
        "pooling" => "Pooling",
        _ => key
    };

    private static void AppendSetting(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.Append(';');
        builder.Append(key).Append('=').Append(value);
    }
}
