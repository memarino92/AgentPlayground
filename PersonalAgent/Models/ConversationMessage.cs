namespace PersonalAgent.Models;

internal record ConversationMessage(string Role, string Content)
{
    public long Sequence { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public AgentPlayground.Contracts.ChatPresentation? Presentation { get; init; }
}
