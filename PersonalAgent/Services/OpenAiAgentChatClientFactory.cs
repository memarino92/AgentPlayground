using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal sealed class OpenAiAgentChatClientFactory(IOptions<ApiKeyOptions> Options) : IAgentChatClientFactory
{
    private readonly OpenAiClientProvider Clients = new(Options);
    private readonly OpenRouterClientProvider OpenRouterClients = new(Options);

    public IChatClient Create(string ModelId)
    {
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
