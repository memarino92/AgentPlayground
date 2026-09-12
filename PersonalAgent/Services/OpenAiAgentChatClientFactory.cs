using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal sealed class OpenAiAgentChatClientFactory(IOptions<ApiKeyOptions> Options) : IAgentChatClientFactory
{
    private readonly OpenAiClientProvider Clients = new(Options);
    public IChatClient Create(string ModelId) => ApplyCompatibility(Clients.Current.GetChatClient(ModelId).AsIChatClient(), ModelId);

    internal static IChatClient ApplyCompatibility(IChatClient Client, string ModelId)
        => ModelId == "gpt-5.6-luna" ? new ConfigureOptionsChatClient(Client, Options =>
        {
            // Luna rejects function tools with reasoning on Chat Completions. Responses is a separate migration.
            if (Options.Tools is { Count: > 0 }) Options.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        }) : Client;
}
