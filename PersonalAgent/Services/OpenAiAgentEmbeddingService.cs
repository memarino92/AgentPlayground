using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal class OpenAiAgentEmbeddingService : IAgentEmbeddingService
{
    private readonly OpenAiClientProvider _clients;
    private readonly string _model;
    private EmbeddingClient EmbeddingClient => _clients.Current.GetEmbeddingClient(_model);

    public OpenAiAgentEmbeddingService(IOptions<ApiKeyOptions> apiKeyOptions, IOptions<AgentMemoryOptions> agentMemoryOptions)
    {
        _clients = new(apiKeyOptions);
        _model = agentMemoryOptions.Value.EmbeddingModel;
    }

    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
        => AgentPlayground.Integrations.AiTelemetry.RunAsync("embedding", "EMBEDDING",
            () => GenerateEmbeddingCoreAsync(content, cancellationToken), _model);

    private async Task<ReadOnlyMemory<float>> GenerateEmbeddingCoreAsync(string content, CancellationToken cancellationToken)
    {
        var response = await EmbeddingClient.GenerateEmbeddingAsync(content, cancellationToken: cancellationToken);
        return response.Value.ToFloats().ToArray();
    }

    public Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default)
        => AgentPlayground.Integrations.AiTelemetry.RunAsync("embedding.batch", "EMBEDDING",
            () => GenerateEmbeddingsCoreAsync(contents, cancellationToken), _model);

    private async Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsCoreAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken)
    {
        if (contents.Count == 0) return [];

        var response = await EmbeddingClient.GenerateEmbeddingsAsync(contents, cancellationToken: cancellationToken);
        var embeddings = new List<ReadOnlyMemory<float>>(contents.Count);

        foreach (var embedding in response.Value)
            embeddings.Add(embedding.ToFloats().ToArray());
        return embeddings;
    }
}
