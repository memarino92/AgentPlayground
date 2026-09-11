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

    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
    {
        var response = await EmbeddingClient.GenerateEmbeddingAsync(content, cancellationToken: cancellationToken);
        return response.Value.ToFloats().ToArray();
    }

    public async Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default)
    {
        if (contents.Count == 0) return [];

        var response = await EmbeddingClient.GenerateEmbeddingsAsync(contents, cancellationToken: cancellationToken);
        var embeddings = new List<ReadOnlyMemory<float>>(contents.Count);

        foreach (var embedding in response.Value)
            embeddings.Add(embedding.ToFloats().ToArray());
        return embeddings;
    }
}
