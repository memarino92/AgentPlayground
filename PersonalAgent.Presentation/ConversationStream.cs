using System.Text.Json;

namespace PersonalAgent.Presentation;

public static class ConversationStream
{
    public static Task<T> ReadAsync<T>(Stream Stream, Func<string, Task> OnDelta, CancellationToken Token) where T : class
        => ReadAsync<T, JsonElement>(Stream, OnDelta, null, Token);

    public static async Task<T> ReadAsync<T, TActivity>(Stream Stream, Func<string, Task> OnDelta,
        Func<TActivity, Task>? OnActivity, CancellationToken Token) where T : class
    {
        using var reader = new StreamReader(Stream, leaveOpen: true);
        while (await reader.ReadLineAsync(Token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var item = JsonSerializer.Deserialize<Event<T, TActivity>>(line, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("The chat stream returned an invalid event.");
            if (item.Type == "activity" && item.Activity is { } activity && OnActivity is not null) await OnActivity(activity);
            if (item.Type == "delta" && item.Delta is not null) await OnDelta(item.Delta);
            if (item.Type == "error") throw new InvalidOperationException(item.Error ?? "The chat stream failed.");
            if (item.Type == "completed" && item.Conversation is not null) return item.Conversation;
        }
        throw new EndOfStreamException("The chat stream ended before completion.");
    }

    private sealed record Event<T, TActivity>(string Type, string? Delta, T? Conversation, string? Error, TActivity? Activity);
}
