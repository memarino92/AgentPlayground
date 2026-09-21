using System.Text.Json;
using AgentPlayground.Contracts;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal partial class AgentChatService
{
    public async Task<SessionConversation> OpenConversationAsync(AgentAccessContext Access, string? ModelId, CancellationToken Token)
    {
        ValidateAccess(Access);
        var Model = string.IsNullOrWhiteSpace(ModelId) ? await _chatModelCatalog.GetDefaultModelAsync(Token)
            : await _chatModelCatalog.FindModelAsync(ModelId, Token) ?? await _chatModelCatalog.GetDefaultModelAsync(Token);
        var Id = await _conversationStore!.EnsureConversationAsync(Access, Model.Id, Token);
        return await ReadConversationAsync(Id, Access, Token) ?? throw new UnauthorizedAccessException();
    }

    public async Task<SessionConversation?> ReadConversationAsync(Guid Id, AgentAccessContext Access, CancellationToken Token)
    {
        var Saved = await AuthorizedConversationAsync(Id, Access, Token);
        if (Saved is null) return null;
        var State = await DeserializeSessionStateAsync(Saved.SessionStateJson, Token);
        var Messages = await _conversationStore!.GetRecentMessagesAsync(Id, State.ContextStartSequence, 100, Token);
        return new(Id.ToString(), State.ModelId, Messages);
    }

    public async Task<bool> ClearConversationAsync(Guid Id, AgentAccessContext Access, CancellationToken Token)
    {
        await using var Guard = await _conversationStore!.LockConversationAsync(Id, Token);
        var Saved = await AuthorizedConversationAsync(Id, Access, Token);
        if (Saved is null) return false;
        var State = await DeserializeSessionStateAsync(Saved.SessionStateJson, Token);
        return await _sessionStore.SetSessionStateAsync(Id, JsonSerializer.Serialize(State with
        { ContextStartSequence = Saved.LastMessageSequence, RecallAfter = DateTimeOffset.UtcNow }), Token);
    }

    public async Task<ChatCard?> UpdateCardAsync(Guid Id, long Sequence, string CardId, ChatCardAction Action, AgentAccessContext Access, CancellationToken Token)
    {
        await using var Guard = await _conversationStore!.LockConversationAsync(Id, Token);
        if (await AuthorizedConversationAsync(Id, Access, Token) is null) throw new UnauthorizedAccessException();
        return await _conversationStore.UpdateCardAsync(Id, Sequence, CardId, Action, Token);
    }

    private async Task<PersistedAgentSession?> AuthorizedConversationAsync(Guid Id, AgentAccessContext Access, CancellationToken Token)
    {
        ValidateAccess(Access);
        var Saved = await _sessionStore.GetSessionAsync(Id, Token);
        if (Saved is null || !string.Equals(Saved.EffectiveActorId, Access.ActorId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Saved.ProfileId, Access.SubjectProfileId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Saved.Role, Access.Role, StringComparison.OrdinalIgnoreCase)) return null;
        var State = await DeserializeSessionStateAsync(Saved.SessionStateJson, Token);
        return State.IsContinuous && State.ScheduledTaskId is null ? Saved : null;
    }
}
