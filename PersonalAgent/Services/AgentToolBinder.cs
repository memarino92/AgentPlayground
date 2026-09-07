using Microsoft.Extensions.AI;

using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class AgentToolBinder(
    IAgentToolRegistry Registry,
    ToolAccessService AccessService,
    IServiceProvider Services,
    ILogger<AgentToolBinder> Logger)
{
    public async Task<IReadOnlyList<BoundAgentTool>> BindAsync(AgentAccessContext Access, CancellationToken CancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Access.ActorId) || string.IsNullOrWhiteSpace(Access.SubjectProfileId) || !AgentRoles.IsDefined(Access.Role))
            throw new InvalidOperationException("A valid server-resolved actor and subject are required to bind tools.");
        var tools = new List<BoundAgentTool>();
        foreach (var registration in Registry.GetRegistrations())
        {
            if (!await AccessService.IsAllowedAsync(Access.Role, registration.Descriptor.Key, CancellationToken)) continue;
            var function = registration.CreateFunction(Services, Access);
            if (!string.Equals(function.Name, registration.Descriptor.Name, StringComparison.Ordinal))
                throw new InvalidOperationException("A tool function name does not match its registration.");
            tools.Add(new BoundAgentTool(registration.Descriptor, registration.Source,
                new LoggingAIFunction(function, Logger, registration.Source, registration.Descriptor.Key, Access, AccessService)));
        }
        return tools.AsReadOnly();
    }
}

internal sealed record BoundAgentTool(AgentToolDescriptor Descriptor, string Source, AIFunction Function);
