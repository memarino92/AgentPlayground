using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class ToolAccessService(IToolAccessStore store, ITavilyMcpToolProvider tavilyMcpToolProvider)
{
    private static readonly AgentToolDescriptor[] LocalTools =
    [
        new(AgentToolKeys.PublishGeneratedTestMessage, "publish_generated_test_message", "Publish test event", "Events", "Publish a generated test message to the shared bus.", true, true, false),
        new(AgentToolKeys.PublishMobileNotification, "publish_mobile_notification", "Send mobile notification", "Notifications", "Send a notification to the current user's registered device.", true, true, false),
        new(AgentToolKeys.SyncWorkJournal, "sync_work_journal", "Sync work journal", "Work journal", "Sync the private work journal from GitHub.", true, true, false),
        new(AgentToolKeys.SearchWorkJournal, "search_work_journal", "Search work journal", "Work journal", "Search the private work journal.", true, true, false),
        new(AgentToolKeys.SearchCoachCheckins, "search_coach_checkins", "Search coach check-ins", "Coach check-ins", "Search assigned athlete coach check-ins.", true, true, true),
        new(AgentToolKeys.ScheduleNotification, "schedule_notification", "Schedule notification", "Notifications", "Schedule a notification for the current user.", true, true, false),
        new(AgentToolKeys.ScheduleAgentTask, "schedule_agent_task", "Schedule agent task", "Scheduling", "Schedule future agent work.", true, true, false),
        new(AgentToolKeys.GetCurrentDateTime, "get_current_date_time", "Current date and time", "Core", "Read the current date and time.", true, true, true)
    ];

    public IReadOnlyList<AgentToolDescriptor> GetTools()
    {
        var webTools = tavilyMcpToolProvider.GetTools()
            .Select(tool => new AgentToolDescriptor(
                AgentToolKeys.Tavily(tool.Name),
                tool.Name,
                tool.Name.Replace('_', ' '),
                "Tavily web search",
                tool.Description,
                tavilyMcpToolProvider.IsAvailable,
                false,
                false));
        return [.. LocalTools, .. webTools];
    }

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
