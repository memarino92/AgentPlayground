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

    public async Task<List<string>> RecallMemoriesAsync(string profileId, string userMessage, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return [];
        if (string.IsNullOrWhiteSpace(profileId)) return [];
        if (string.IsNullOrWhiteSpace(userMessage)) return [];

        var embedding = await embeddingService.GenerateEmbeddingAsync(userMessage, cancellationToken);
        var memories = await semanticMemoryStore.SearchMemoriesAsync(profileId, embedding, limit: 5, cancellationToken);
        return memories
            .Where(memory => memory.Distance <= 0.55d)
            .Where(memory => !string.Equals(memory.Content, userMessage, StringComparison.Ordinal))
            .Select(memory => memory.Content)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task StoreConversationMemoriesAsync(Guid sessionId, string profileId, string userMessage, string assistantMessage, CancellationToken cancellationToken = default)
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(profileId)) return;

        if (ShouldStoreUserMemory(userMessage))
            await StoreMessageMemoryAsync(sessionId, profileId, "user", userMessage, cancellationToken);
    }

    private async Task StoreMessageMemoryAsync(Guid sessionId, string profileId, string memoryKind, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        try
        {
            var embedding = await embeddingService.GenerateEmbeddingAsync(content, cancellationToken);
            await semanticMemoryStore.AddMemoryAsync(sessionId, profileId, memoryKind, content, embedding, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store semantic memory for session {SessionId} and profile {ProfileId}", sessionId, profileId);
        }
    }

    private static bool ShouldStoreUserMemory(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return false;
        if (userMessage.Length > 500) return false;

        var normalized = userMessage.Trim().ToLowerInvariant();
        var factSignals = new[]
        {
            "my favorite",
            "i like",
            "i love",
            "i prefer",
            "my name is",
            "my dog's name",
            "my wife",
            "my husband",
            "i work",
            "i live",
            "remember that",
            "it's ",
            "it is "
        };

        return factSignals.Any(signal => normalized.Contains(signal, StringComparison.Ordinal));
    }
}
