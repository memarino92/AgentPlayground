using Microsoft.Extensions.AI;

using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal sealed record AgentToolRegistration(
    AgentToolDescriptor Descriptor,
    string Source,
    Func<IServiceProvider, AgentAccessContext, AIFunction> CreateFunction);

internal interface IAgentToolRegistry
{
    IReadOnlyList<AgentToolRegistration> GetRegistrations();
}
