namespace PersonalAgent.Services;

internal interface IAgentSemanticMemoryStore
{
    Task AddMemoryAsync(Guid sessionId, string profileId, string memoryKind, string content, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default);
    Task<List<MemoryRecord>> SearchMemoriesAsync(string profileId, ReadOnlyMemory<float> embedding, int limit, CancellationToken cancellationToken = default);
}
