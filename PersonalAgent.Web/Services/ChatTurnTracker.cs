namespace PersonalAgent.Web.Services;

// Scoped to the signed-in Blazor circuit so navigation can reattach to an active turn.
internal sealed class ChatTurnTracker
{
    private readonly Dictionary<string, ChatTurn> turns = [];

    public IReadOnlyDictionary<string, ChatTurn> Snapshot() => new Dictionary<string, ChatTurn>(turns);

    public ChatTurn? Find(string sessionId) => turns.GetValueOrDefault(sessionId);

    public ChatTurn Start(string sessionId, string message, PersonalAgentClient client, string profileId)
    {
        var turn = new ChatTurn(message);
        turns.Add(sessionId, turn);
        turn.Completion = RunAsync();
        return turn;

        async Task<HistoryResponse> RunAsync()
        {
            try
            {
                return await client.SendConversationMessageStreamingAsync(sessionId, profileId, message, turn.AppendAsync, turn.Cancellation.Token);
            }
            finally
            {
                turns.Remove(sessionId);
                turn.Cancellation.Dispose();
            }
        }
    }
}

internal sealed class ChatTurn(string message)
{
    private readonly System.Text.StringBuilder reply = new();
    public string Message { get; } = message;
    public string Reply => reply.ToString();
    public CancellationTokenSource Cancellation { get; } = new();
    public Task<HistoryResponse> Completion { get; set; } = null!;
    public event Func<Task>? Changed;

    public Task AppendAsync(string delta)
    {
        reply.Append(delta);
        return Changed?.Invoke() ?? Task.CompletedTask;
    }
}
