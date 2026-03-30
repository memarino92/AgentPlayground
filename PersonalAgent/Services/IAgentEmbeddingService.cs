namespace PersonalAgent.Services;

internal interface IAgentEmbeddingService
{
    Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default);
}
