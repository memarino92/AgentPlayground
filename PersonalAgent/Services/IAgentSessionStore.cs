using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface IAgentSessionStore
{
    Task CreateSessionAsync(Guid sessionId, string profileId, string sessionStateJson, CancellationToken cancellationToken = default);
    Task<PersistedAgentSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<bool> SaveInteractionAsync(Guid sessionId, string userMessage, string assistantMessage, string sessionStateJson, CancellationToken cancellationToken = default);
    Task<List<ConversationMessage>?> GetSessionMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
