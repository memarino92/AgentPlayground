using PersonalAgent.Contracts;

namespace PersonalAgent.Web.Services;

// Scoped to the signed-in Blazor circuit so navigation can reattach to an active turn.
internal sealed class ChatTurnTracker : IDisposable
{
    private readonly Dictionary<string, ChatTurn> turns = [];

    public void Dispose()
    {
        foreach (var turn in turns.Values.ToArray()) turn.Cancellation.Cancel();
    }

    public IReadOnlyDictionary<string, ChatTurn> Snapshot() => new Dictionary<string, ChatTurn>(turns);

    public ChatTurn? Find(string sessionId) => turns.GetValueOrDefault(sessionId);

    public ChatTurn Start(string sessionId, string message, PersonalAgentClient client, string profileId, Guid? existingRun = null)
    {
        var turn = new ChatTurn(existingRun ?? Guid.NewGuid(), message);
        turns.Add(sessionId, turn);
        turn.Completion = RunAsync();
        return turn;

        async Task<HistoryResponse> RunAsync()
        {
            try
            {
                return await client.SendConversationMessageStreamingAsync(sessionId, profileId, message, turn.AppendAsync, turn.Cancellation.Token, turn.UpdateActivityAsync, turn.Id, existingRun.HasValue);
            }
            finally
            {
                turns.Remove(sessionId);
                turn.Cancellation.Dispose();
            }
        }
    }
}

internal sealed class ChatTurn(Guid id, string message)
{
    private readonly System.Text.StringBuilder reply = new();
    public Guid Id { get; } = id;
    public string Message { get; } = message;
    public string Reply => reply.ToString();
    public CancellationTokenSource Cancellation { get; } = new();
    public Task<HistoryResponse> Completion { get; set; } = null!;
    public event Func<Task>? Changed;

    private readonly Dictionary<Guid, ChatToolActivity> activity = [];
    public ChatPresentation Presentation => new() { Activity = [.. activity.Values] };

    public Task UpdateActivityAsync(ChatToolActivity item)
    {
        activity[item.CallId] = item;
        return Changed?.Invoke() ?? Task.CompletedTask;
    }

    public Task AppendAsync(string delta)
    {
        reply.Append(delta);
        return Changed?.Invoke() ?? Task.CompletedTask;
    }
}
