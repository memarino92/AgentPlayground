using Microsoft.Extensions.AI;

using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed record AgentToolRegistration(
    AgentToolDescriptor Descriptor,
    string Source,
    Func<IServiceProvider, AgentAccessContext, AIFunction> CreateFunction);

internal interface IAgentToolRegistry
{
    IReadOnlyList<AgentToolRegistration> GetRegistrations();
}
