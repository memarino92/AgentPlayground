namespace PersonalAgent.Models;

/// <summary>Chat models eligible for new sessions, selected by the API.</summary>
internal sealed record ChatModelsResponse(IReadOnlyList<AvailableChatModel> Models);
