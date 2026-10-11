using Microsoft.Extensions.AI;

namespace PersonalAgent.Api.Services;

internal interface IAgentChatClientFactory
{
    IChatClient Create(string ModelId);
    IChatClient CreateForActor(string ModelId, string ActorId) => Create(ModelId);
}
