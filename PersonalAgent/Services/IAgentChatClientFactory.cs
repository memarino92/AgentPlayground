using Microsoft.Extensions.AI;

namespace PersonalAgent.Services;

internal interface IAgentChatClientFactory
{
    IChatClient Create(string ModelId);
}
