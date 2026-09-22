using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal interface IToolAccessStore
{
    Task<IReadOnlyDictionary<string, bool>> GetRolePermissionsAsync(string role, CancellationToken cancellationToken = default);
    Task SavePermissionsAsync(IReadOnlyList<ToolRolePermission> permissions, string updatedBy, CancellationToken cancellationToken = default);
}
