using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class ToolAccessService(IToolAccessStore store, IAgentToolRegistry registry)
{
    public IReadOnlyList<AgentToolDescriptor> GetTools() =>
        registry.GetRegistrations().Select(Registration => Registration.Descriptor).ToArray();

    public async Task<bool> IsAllowedAsync(string role, string toolKey, CancellationToken cancellationToken = default)
    {
        if (!AgentRoles.IsDefined(role)) return false;
        var descriptor = GetTools().FirstOrDefault(tool => tool.Key == toolKey);
        if (descriptor is null || !descriptor.IsAvailable) return false;
        var overrides = await store.GetRolePermissionsAsync(role, cancellationToken);
        return overrides.TryGetValue(toolKey, out var enabled)
            ? enabled
            : role == AgentRoles.Owner ? descriptor.OwnerDefault : descriptor.CoachDefault;
    }

    public async Task<ToolAccessCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var tools = GetTools();
        var permissions = new List<ToolRolePermission>();
        foreach (var role in AgentRoles.Defined)
        {
            var overrides = await store.GetRolePermissionsAsync(role, cancellationToken);
            permissions.AddRange(tools.Select(tool => new ToolRolePermission(
                role,
                tool.Key,
                overrides.TryGetValue(tool.Key, out var enabled)
                    ? enabled
                    : role == AgentRoles.Owner ? tool.OwnerDefault : tool.CoachDefault)));
        }
        return new ToolAccessCatalog(tools, permissions);
    }

    public async Task SaveAsync(SaveToolAccessRequest request, CancellationToken cancellationToken = default)
    {
        var knownKeys = GetTools().Select(tool => tool.Key).ToHashSet(StringComparer.Ordinal);
        if (request.Permissions.Any(permission => !AgentRoles.IsDefined(permission.Role) || !knownKeys.Contains(permission.ToolKey)))
            throw new InvalidOperationException("The tool access update contains an unknown role or tool.");
        await store.SavePermissionsAsync(request.Permissions, request.UpdatedBy, cancellationToken);
    }
}
