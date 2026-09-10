using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;

using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal sealed class OpenAiAgentChatClientFactory(IOptions<ApiKeyOptions> Options) : IAgentChatClientFactory
{
    private readonly OpenAIClient Client = new(Options.Value.OpenAiKey);
    public IChatClient Create(string ModelId) => Client.GetChatClient(ModelId).AsIChatClient();
}
