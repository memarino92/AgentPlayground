using AgentPlayground.Contracts;

namespace PersonalAgent.Web.Components.Pages;

public sealed record ChatCardInteraction(long Sequence, string CardId, ChatCardAction Action);
