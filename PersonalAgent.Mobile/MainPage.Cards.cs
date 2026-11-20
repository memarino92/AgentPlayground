using System.Text.Json;

using PersonalAgent.Contracts;
using PersonalAgent.Mobile.Services;
using PersonalAgent.Presentation;

namespace PersonalAgent.Mobile;

public partial class MainPage
{
    private void OnMessageActionsChanged(object? Sender, EventArgs Args)
    {
        if (Sender is not VerticalStackLayout layout) return;
        layout.Clear();
        if (layout.BindingContext is not NativeChatMessage message) return;
        void Action(string text, Func<Task> invoke)
        {
            var button = new Button { Text = text };
            button.Clicked += async (_, _) =>
            {
                if (_chatBusy) return;
                button.IsEnabled = false;
                try { await invoke(); }
                catch (Exception) { ChatStatus.Text = "Could not complete the action. Refresh and try again."; }
                finally { button.IsEnabled = true; }
            };
            layout.Add(button);
        }

        if (message.Presentation?.Activity is { Count: > 0 } activity)
        {
            var rows = new VerticalStackLayout { IsVisible = message.Sequence == 0 };
            foreach (var call in activity)
            {
                var detail = new Label { Text = $"{call.Label} · {call.Status}" };
                detail.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#162C29"), Color.FromArgb("#E9F2EE"));
                rows.Add(detail);
            }
            var toggle = new Button { Text = $"View activity · {activity.Count}" };
            toggle.Clicked += (_, _) => rows.IsVisible = !rows.IsVisible;
            layout.Add(toggle);
            layout.Add(rows);
        }
        foreach (var link in EvidenceCitation.Parse(message.Content, _chat.Profile))
            if (link.ProfileId == _chat.Profile)
                Action("Open recording evidence", () => Navigation.PushAsync(new WorkspacePage(_chat, "Recording evidence", link.UploadId, StartMs: link.StartMs)));
        foreach (var source in message.Presentation?.Sources ?? [])
            Action(source.Excerpt, () => Navigation.PushAsync(new WorkspacePage(_chat, "Conversation history", source.SessionId)));
        foreach (var card in message.Presentation?.Cards ?? [])
        {
            layout.Add(new Label { Text = card.Title, FontAttributes = FontAttributes.Bold });
            if (card.Detail is not null) layout.Add(new Label { Text = card.Detail });
            Task Update(ChatCardAction edit) => ApplyCardAsync(message.Sequence, card, edit);
            switch (card.Kind)
            {
                case "commitment":
                    if (card.CommitmentAction() is { } action)
                        Action(action.Action == "confirm" ? "Track this" : "Mark done", () => Update(action));
                    else layout.Add(new Label { Text = card.Status });
                    break;
                case "checklist":
                    foreach (var item in card.Items) Action($"{(item.Done ? "☑" : "☐")} {item.Text}", () => Update(card.Toggle(item.Id)));
                    break;
                case "clarification" when card.Answer is null:
                    foreach (var option in card.Options) Action(option, () => Update(card.Respond(option)));
                    Action("Write an answer", async () =>
                    {
                        var answer = await DisplayPromptAsync(card.Title, "Your answer", maxLength: 1000);
                        if (!string.IsNullOrWhiteSpace(answer)) await Update(card.Respond(answer));
                    }); break;
                case "clarification": layout.Add(new Label { Text = card.Answer }); break;
            }
        }
    }

    private async Task ApplyCardAsync(long Sequence, ChatCard Card, ChatCardAction Action)
    {
        if (_conversation is not Guid id || _chatBusy) return;
        _chatBusy = true;
        try
        {
            await _chat.WorkspaceAsync("card", id, Card.Id, JsonSerializer.SerializeToNode(Action), Sequence);
        }
        finally { _chatBusy = false; }
        if (Action.Action == "answer" && string.IsNullOrWhiteSpace(Draft.Text))
        {
            Draft.Text = $"{Card.Title}\nMy answer: {Action.Value}";
            await LoadChatAsync(message: Draft.Text);
        }
        else
        {
            if (Action.Action == "answer") Draft.Text = $"{Card.Title}\nMy answer: {Action.Value}\n\n{Draft.Text}";
            await LoadChatAsync();
        }
    }
}
