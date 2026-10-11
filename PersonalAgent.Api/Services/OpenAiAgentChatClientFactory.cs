using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

using PersonalAgent.Api.Configuration;

namespace PersonalAgent.Api.Services;

internal sealed class OpenAiAgentChatClientFactory(IOptions<ApiKeyOptions> Options,
    PersonalAgent.Integrations.ChatGpt.ChatGptConnectionStore? ChatGpt = null) : IAgentChatClientFactory
{
    private readonly OpenAiClientProvider Clients = new(Options);
    private readonly OpenRouterClientProvider OpenRouterClients = new(Options);

    public IChatClient CreateForActor(string ModelId, string ActorId)
        => ChatGptModelIds.TryParse(ModelId, out var connection, out var slug)
            ? new ObservableChatClient(new ChatGptChatClient(ChatGpt ?? throw new InvalidOperationException("ChatGPT is unavailable."), ActorId, connection, slug), ModelId)
            : Create(ModelId);

    public IChatClient Create(string ModelId)
    {
        if (ModelId.StartsWith("chatgpt:", StringComparison.Ordinal)) throw new InvalidOperationException("ChatGPT requires the authorized actor.");
        var client = OpenRouterModelIds.TryGetProviderId(ModelId, out var providerModelId)
            ? OpenRouterClients.Current.GetChatClient(providerModelId).AsIChatClient()
            : Clients.Current.GetChatClient(ModelId).AsIChatClient();
        return new ObservableChatClient(ApplyCompatibility(client, ModelId), ModelId);
    }

    internal static IChatClient ApplyCompatibility(IChatClient Client, string ModelId)
        => ModelId is "gpt-5.6-luna" or "gpt-5.6-terra" ? new ConfigureOptionsChatClient(Client, Options =>
        {
            // Luna and Terra reject function tools with reasoning on Chat Completions. Responses is a separate migration.
            if (Options.Tools is { Count: > 0 }) Options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        }) : Client;
}
