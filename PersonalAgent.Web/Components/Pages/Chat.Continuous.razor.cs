using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class Chat
{
    private bool isSearchingHistory;
    private List<AgentPlayground.Contracts.ChatContextSource>? historyResults;

    private async Task SearchHistoryAsync(string Query)
    {
        if (profileId is null || isSearchingHistory) return;
        isSearchingHistory = true;
        try { historyResults = await ApiClient.SearchConversationHistoryAsync(profileId, Query); }
        catch (Exception Error) { errorMessage = Error.Message; }
        finally { isSearchingHistory = false; }
    }

    private async Task OpenConversationAsync()
    {
        if (profileId is null) return;
        isLoadingHistory = true;
        try
        {
            var History = await ApiClient.OpenConversationAsync(profileId, selectedModelId);
            sessionId = History.SessionId;
            activeModelId = selectedModelId = History.ModelId;
            messages = [.. History.Messages];
            isContinuous = true;
            isReadOnly = false;
            shouldScrollToBottom = true;
            SaveChatQuery();
        }
        finally { isLoadingHistory = false; }
    }

    private async Task ReturnToConversationAsync()
    {
        if (isBusy) return;
        try { errorMessage = null; await OpenConversationAsync(); }
        catch (Exception Error) { errorMessage = Error.Message; }
    }

    private async Task HandleCardActionAsync(ChatCardInteraction Interaction)
    {
        if (isBusy || isReadOnly || !isContinuous || sessionId is null || profileId is null) return;
        isUpdatingCard = true;
        string? Reply = null;
        try
        {
            var Card = await ApiClient.UpdateChatCardAsync(sessionId, profileId, Interaction.Sequence, Interaction.CardId, Interaction.Action);
            messages = messages.Select(Message => Message.Sequence == Interaction.Sequence && Message.Presentation is { } Presentation
                ? Message with { Presentation = Presentation with { Cards = Presentation.Cards.Select(Value => Value.Id == Card.Id ? Card : Value).ToList() } }
                : Message).ToList();
            if (Interaction.Action.Action == "answer") Reply = $"{Card.Title}\nMy answer: {Card.Answer}";
            errorMessage = null;
        }
        catch (Exception Error) { errorMessage = Error.Message; }
        finally { isUpdatingCard = false; }
        if (Reply is null) return;
        // Preserve an existing draft; otherwise submit through the ordinary authorized chat path.
        if (!string.IsNullOrWhiteSpace(currentMessage))
        {
            currentMessage = Reply + "\n\n" + currentMessage;
            notice = "Your answer is saved and added to your draft. Send it when you're ready.";
            return;
        }
        currentMessage = Reply;
        await SendMessage();
    }
}
