namespace PersonalAgent.Models;

internal record AgentToolDescriptor(
    string Key,
    string Name,
    string DisplayName,
    string Integration,
    string Description,
    bool IsAvailable,
    bool OwnerDefault,
    bool CoachDefault);

internal record ToolRolePermission(string Role, string ToolKey, bool IsEnabled);

internal record ToolAccessCatalog(IReadOnlyList<AgentToolDescriptor> Tools, IReadOnlyList<ToolRolePermission> Permissions);

internal record SaveToolAccessRequest(IReadOnlyList<ToolRolePermission> Permissions, string UpdatedBy);

internal static class AgentToolKeys
{
    public const string PublishGeneratedTestMessage = "Local:publish_generated_test_message";
    public const string PublishMobileNotification = "Local:publish_mobile_notification";
    public const string SyncWorkJournal = "Local:sync_work_journal";
    public const string SearchWorkJournal = "Local:search_work_journal";
    public const string SearchCoachCheckins = "Local:search_coach_checkins";
    public const string ScheduleNotification = "Local:schedule_notification";
    public const string ScheduleAgentTask = "Local:schedule_agent_task";
    public const string GetCurrentDateTime = "Local:get_current_date_time";

    public static string Tavily(string name) => $"TavilyMcp:{name}";
}
