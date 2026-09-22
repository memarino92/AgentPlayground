using PersonalAgent.Contracts;
using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal record HistoricalTurn(ChatContextSource Source, string UserText, string AssistantText);

internal interface IConversationContextStore
{
    Task<IAsyncDisposable> LockConversationAsync(Guid Id, CancellationToken Token);
    Task<Guid> EnsureConversationAsync(AgentAccessContext Access, string ModelId, CancellationToken Token);
    Task<IReadOnlyList<PersistedAgentSessionSummary>> GetScopedSessionsAsync(AgentAccessContext Access, DateTimeOffset? Before, Guid? BeforeId, int Limit, CancellationToken Token);
    Task<List<ConversationMessage>> GetRecentMessagesAsync(Guid Id, long AfterSequence, int Limit, CancellationToken Token);
    Task<List<HistoricalTurn>> SearchHistoryAsync(AgentAccessContext Access, Guid CurrentId, long BeforeSequence, DateTimeOffset? After,
        string Query, ReadOnlyMemory<float>? Embedding, CancellationToken Token);
    Task IndexTurnAsync(Guid Id, long Sequence, string ProfileId, string Content, ReadOnlyMemory<float> Embedding, CancellationToken Token);
    Task<bool> SavePresentedInteractionAsync(Guid Id, string User, string Assistant, string State, ChatPresentation Presentation, CancellationToken Token);
    Task<ChatCard?> UpdateCardAsync(Guid Id, long Sequence, string CardId, ChatCardAction Action, CancellationToken Token);
}
