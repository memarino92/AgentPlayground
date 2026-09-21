using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentPlayground.Contracts;

public sealed record ChatContextSource(Guid SessionId, long Sequence, DateTimeOffset CreatedAt, string Excerpt);
public sealed record ChatCardItem(string Id, string Text, bool Done = false);
public sealed record ChatCard(string Id, string Kind, string Title)
{
    public string? Detail { get; init; }
    public string Status { get; init; } = "proposed";
    public List<ChatCardItem> Items { get; init; } = [];
    public List<string> Options { get; init; } = [];
    public string? Answer { get; init; }
    public int Revision { get; init; }
}

public sealed record ChatCardAction(int Revision, string Action, string? Value = null);
public sealed record ChatPresentation
{
    public List<ChatCard> Cards { get; init; } = [];
    public List<ChatContextSource> Sources { get; init; } = [];
}

// Only a tiny, data-only vocabulary crosses the model/UI boundary. Unknown or invalid blocks remain text.
public static partial class ChatCardParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static (string Text, List<ChatCard> Cards) Parse(string Content)
    {
        var Cards = new List<ChatCard>();
        var Text = CardBlock().Replace(Content, Match =>
        {
            if (Cards.Count >= 3 || Match.Groups[1].Length > 12000) return Match.Value;
            try
            {
                var Card = JsonSerializer.Deserialize<ChatCard>(Match.Groups[1].Value, Options);
                if (Card is null || !IsValid(Card)) return Match.Value;
                Cards.Add(Card with
                {
                    Id = Guid.NewGuid().ToString("N"), Status = "proposed", Revision = 0, Answer = null,
                    Items = Card.Items.Select(Item => new ChatCardItem(Guid.NewGuid().ToString("N"), Item.Text)).ToList()
                });
                return string.Empty;
            }
            catch (JsonException) { return Match.Value; }
        });
        return (Text.Trim(), Cards);
    }

    public static bool IsValid(ChatCard Card) =>
        Card.Kind is "commitment" or "checklist" or "clarification"
        && !string.IsNullOrWhiteSpace(Card.Title) && Card.Title.Length <= 200
        && (Card.Detail?.Length ?? 0) <= 1500
        && Card.Items is { Count: <= 20 } && Card.Items.All(Item => Item is not null && !string.IsNullOrWhiteSpace(Item.Text) && Item.Text.Length <= 300)
        && Card.Options is { Count: <= 6 } && Card.Options.All(Option => !string.IsNullOrWhiteSpace(Option) && Option.Length <= 200)
        && (Card.Kind != "checklist" || Card.Items.Count > 0);

    public static ChatCard? Apply(ChatCard Card, ChatCardAction Action)
    {
        if (Action.Revision != Card.Revision) return null;
        var Updated = (Card.Kind, Action.Action) switch
        {
            ("commitment", "confirm") when Card.Status == "proposed" => Card with { Status = "active" },
            ("commitment", "complete") when Card.Status == "active" => Card with { Status = "done" },
            ("checklist", "toggle") when Card.Items.Any(Item => Item.Id == Action.Value) => Card with
            { Items = Card.Items.Select(Item => Item.Id == Action.Value ? Item with { Done = !Item.Done } : Item).ToList() },
            ("clarification", "answer") when Card.Answer is null && !string.IsNullOrWhiteSpace(Action.Value) && Action.Value.Length <= 1000 =>
                Card with { Answer = Action.Value.Trim(), Status = "answered" },
            _ => null
        };
        return Updated is null ? null : Updated with { Revision = Card.Revision + 1 };
    }

    [GeneratedRegex("```garden-card\\s*\\r?\\n([\\s\\S]*?)\\r?\\n```", RegexOptions.CultureInvariant)]
    private static partial Regex CardBlock();
}
