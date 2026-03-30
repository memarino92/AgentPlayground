using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class SemanticMemoryService(
    IAgentEmbeddingService embeddingService,
    IAgentSemanticMemoryStore semanticMemoryStore,
    IOptions<AgentMemoryOptions> options,
    ILogger<SemanticMemoryService> logger)
{
    private readonly AgentMemoryOptions _options = options.Value;

    public bool Enabled => _options.EnableSemanticMemory;

    public async Task<List<string>> RecallMemoriesAsync(Guid sessionId, string userMessage, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return [];
        if (string.IsNullOrWhiteSpace(userMessage)) return [];

        var embedding = await embeddingService.GenerateEmbeddingAsync(userMessage, cancellationToken);
        var memories = await semanticMemoryStore.SearchMemoriesAsync(sessionId, embedding, limit: 3, cancellationToken);
        return memories
            .Where(memory => memory.Distance <= 0.35d)
            .Where(memory => !string.Equals(memory.Content, userMessage, StringComparison.Ordinal))
            .Select(memory => memory.Content)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task StoreConversationMemoriesAsync(Guid sessionId, string userMessage, string assistantMessage, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;

        await StoreMessageMemoryAsync(sessionId, "user", userMessage, cancellationToken);

        if (ShouldStoreAssistantMemory(assistantMessage))
            await StoreMessageMemoryAsync(sessionId, "assistant", assistantMessage, cancellationToken);
    }

    private async Task StoreMessageMemoryAsync(Guid sessionId, string memoryKind, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        try
        {
            var embedding = await embeddingService.GenerateEmbeddingAsync(content, cancellationToken);
            await semanticMemoryStore.AddMemoryAsync(sessionId, memoryKind, content, embedding, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store semantic memory for session {SessionId}", sessionId);
        }
    }

    private static bool ShouldStoreAssistantMemory(string assistantMessage) =>
        !string.IsNullOrWhiteSpace(assistantMessage) && assistantMessage.Length <= 2000;
}
