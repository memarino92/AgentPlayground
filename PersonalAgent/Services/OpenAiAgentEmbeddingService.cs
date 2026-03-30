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
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key not found. In production set OPENAI_API_KEY env var; for local dev use: dotnet user-secrets set OpenApiKey \"your-key\" --project PersonalAgent");

        _embeddingClient = new OpenAIClient(apiKey).GetEmbeddingClient(agentMemoryOptions.Value.EmbeddingModel);
    }

    public async Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default)
    {
        var response = await _embeddingClient.GenerateEmbeddingAsync(content, cancellationToken: cancellationToken);
        return response.Value.ToFloats().ToArray();
    }
}
