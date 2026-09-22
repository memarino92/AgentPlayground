using Microsoft.Extensions.AI;

namespace PersonalAgent.Api.Services;

internal interface IAgentChatClientFactory
{
    IChatClient Create(string ModelId);
}
