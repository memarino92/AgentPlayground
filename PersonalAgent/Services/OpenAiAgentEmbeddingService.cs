using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal class OpenAiAgentEmbeddingService : IAgentEmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;

    public OpenAiAgentEmbeddingService(IOptions<ApiKeyOptions> apiKeyOptions, IOptions<AgentMemoryOptions> agentMemoryOptions)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        _embeddingClient = new OpenAIClient(apiKey).GetEmbeddingClient(agentMemoryOptions.Value.EmbeddingModel);
    }

    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
    {
        var response = await _embeddingClient.GenerateEmbeddingAsync(content, cancellationToken: cancellationToken);
        return response.Value.ToFloats().ToArray();
    }

    public async Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default)
    {
        if (contents.Count == 0) return [];

        var response = await _embeddingClient.GenerateEmbeddingsAsync(contents, cancellationToken: cancellationToken);
        var embeddings = new List<ReadOnlyMemory<float>>(contents.Count);

        foreach (var embedding in response.Value)
            embeddings.Add(embedding.ToFloats().ToArray());
        return embeddings;
    }
}
