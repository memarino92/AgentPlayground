namespace PersonalAgent.Contracts;

public sealed record ChatContextSource(Guid SessionId, long Sequence, DateTimeOffset CreatedAt, string Excerpt);
public sealed record ChatCardItem(string Id, string Text, bool Done = false);
public sealed record ChatCard(string Id, string Kind, string Title)
{
    public ChatCardAction? CommitmentAction() => Status switch
    {
        "proposed" => new(Revision, "confirm"),
        "active" => new(Revision, "complete"),
        _ => null
    };
    public ChatCardAction Toggle(string ItemId) => new(Revision, "toggle", ItemId);
    public ChatCardAction Respond(string AnswerText) => new(Revision, "answer", AnswerText);

    public string? Detail { get; init; }
    public string Status { get; init; } = "proposed";
    public List<ChatCardItem> Items { get; init; } = [];
    public List<string> Options { get; init; } = [];
    public string? Answer { get; init; }
    public int Revision { get; init; }
}

public sealed record ChatCardAction(int Revision, string Action, string? Value = null);
public sealed record ChatToolActivity(Guid CallId, string Label, string Status);

public sealed record ChatPresentation
{
    public List<ChatToolActivity> Activity { get; init; } = [];
    public List<ChatCard> Cards { get; init; } = [];
    public List<ChatContextSource> Sources { get; init; } = [];
}
