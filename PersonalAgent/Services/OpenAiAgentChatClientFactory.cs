using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal sealed class OpenAiAgentChatClientFactory(IOptions<ApiKeyOptions> Options) : IAgentChatClientFactory
{
    private readonly OpenAiClientProvider Clients = new(Options);
    public IChatClient Create(string ModelId) => Clients.Current.GetChatClient(ModelId).AsIChatClient();
}
