namespace PersonalAgent.Services;

internal interface IAgentSemanticMemoryStore
{
    Task AddMemoryAsync(Guid sessionId, string memoryKind, string content, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default);
    Task<List<MemoryRecord>> SearchMemoriesAsync(Guid sessionId, ReadOnlyMemory<float> embedding, int limit, CancellationToken cancellationToken = default);
}
