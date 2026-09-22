namespace PersonalAgent.Api.Models;

internal record ConversationMessage(string Role, string Content)
{
    public long Sequence { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public PersonalAgent.Contracts.ChatPresentation? Presentation { get; init; }
}
