using Microsoft.Extensions.AI;

using PersonalAgent.Api.Models;
using PersonalAgent.Api.Automations;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Services;

// API composition boundary: metadata and the context-bound handler are declared together.
internal sealed class AgentToolRegistry(ITavilyMcpToolProvider TavilyProvider) : IAgentToolRegistry
{
    private static readonly IReadOnlyList<AgentToolRegistration> LocalTools = Array.AsReadOnly<AgentToolRegistration>(
    [
        Local(AutomationPrograms.PermissionKey, "C# automation programs", "Automations",
            "Discover how to include a csharp step in a saved automation. Call automation_catalog for the schema, then save_automation. Source is a single net11.0 C# program with BCL only: read Console.In, write Console.Out. No packages, networking or credentials. The dedicated runner must be deployed. Inspect build/run diagnostics before claiming completion.",
            false, false, false, (Services, Access) => () => Services.GetRequiredService<AutomationRecipes>().Catalog(Services, Access)),
        Local("Local:automation_catalog", "Automation actions", "Automations",
            "Discover the recipe format and supported deterministic automation actions. Call before creating an automation. Recipes run without a model; do not promise unsupported scripts or integrations.",
            true, false, false, (Services, Access) => () => Services.GetRequiredService<AutomationRecipes>().Catalog(Services, Access)),
        Local(AutomationAuthorization.ManageKey, "Create or revise automation", "Automations",
            "Create a deterministic registered-action JSON recipe, or revise an existing automation with automationId and expectedVersion. First discover automation_catalog. Name and source are required. Omit executeAt for an immediate run, or provide an ISO-8601 timestamp with offset. Optional repeatEvery is a fixed ISO duration (PT1M–P366D), not calendar/cron recurrence. A revision replaces future source and schedule; running executions keep their version. Return the dashboard link. Use this for reusable deterministic work, rather than scheduling a future agent conversation.",
            true, false, true, (Services, Access) => (string name, string source, Guid? automationId, int? expectedVersion, string? executeAt, string? repeatEvery, CancellationToken token) =>
                Services.GetRequiredService<AutomationTools>().SaveAsync(Access, name, source, automationId, expectedVersion, executeAt, repeatEvery, token)),
        Local("Local:list_automations", "List automations", "Automations", "List the current subject's deterministic automations and next scheduled executions.",
            true, false, false, (Services, Access) => (CancellationToken token) => Services.GetRequiredService<AutomationTools>().ListAsync(Access, token)),
        Local("Local:inspect_automation", "Inspect automation", "Automations", "Inspect an automation's versions, source and run history, or provide runId to inspect step outputs, errors and saved reports.",
            true, false, false, (Services, Access) => (Guid automationId, Guid? runId, CancellationToken token) => Services.GetRequiredService<AutomationTools>().InspectAsync(Access, automationId, runId, token)),
        Local("Local:control_automation", "Control automation", "Automations", "Run an automation now, pause future runs, or resume it. operation must be run, pause, or resume. Pausing does not interrupt an already running execution.",
            true, false, true, (Services, Access) => (Guid automationId, string operation, CancellationToken token) => Services.GetRequiredService<AutomationTools>().ControlAsync(Access, automationId, operation, token)),
        Local(AgentToolKeys.PublishMobileNotification, "Send mobile notification", "Notifications",
            "Send a push notification to the current user's registered mobile device. Use this when the user asks to notify or ping their phone.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (string title, string body, CancellationToken token) =>
                Services.GetRequiredService<AgentEventService>().PublishMobileNotificationToolAsync(Access.SubjectProfileId, title, body, token)),
        Local(AgentToolKeys.SyncWorkJournal, "Sync work journal", "Work journal",
            "Trigger a background process to sync the private work journal from GitHub and prepare it for semantic search.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, _) => Services.GetRequiredService<WorkJournalService>().SyncWorkJournalAsync),
        Local(AgentToolKeys.SearchWorkJournal, "Search work journal", "Work journal",
            "Search the private work journal for answers about past work, journal entries, or questions like 'when did I work on...' or 'who did I help'.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: false,
            (Services, _) => Services.GetRequiredService<WorkJournalService>().SearchWorkJournalAsync),
        Local(AgentToolKeys.SearchCoachCheckins, "Search coach check-ins", "Coach check-ins",
            "Search transcribed coach check-ins for exercise cues, notes, and attributed coaching advice. Provide a focused query and optionally an exerciseTag like yoke or squat as a relevance hint. When the user names a recording, pass its exact filename in fileName. Use recency=latest for the most recent/latest/last call (selected before topic search), recent for general advice without an explicit latest-call request, relevance for historical comparisons. Athlete scope is applied by the server.",
            OwnerDefault: true, CoachDefault: true, HasSideEffects: false,
            (Services, Access) => (string query, CancellationToken token, string? exerciseTag = null, string? fileName = null, string? recency = null) =>
                Services.GetRequiredService<CoachCheckinService>().SearchCoachCheckinsAsync(query, Access.SubjectProfileId, exerciseTag, token, fileName, recency)),
        Local(AgentToolKeys.ScheduleNotification, "Schedule notification", "Notifications",
            "Schedule a mobile notification for the current user using delay, absolute executeAt datetime, or natural when text like 'tonight'. Set repeatEvery to an ISO-8601 duration such as P1D or P7D to make it recurring.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (string title, string body, string? delay, string? executeAt, string? when, string? timeZoneId, string? repeatEvery, CancellationToken token) =>
                Services.GetRequiredService<AgentEventService>().ScheduleNotificationToolAsync(Access, title, body, delay, executeAt, when, timeZoneId, repeatEvery, token)),
        Local(AgentToolKeys.ScheduleAgentTask, "Schedule agent task", "Scheduling",
            "Schedule a future agent task. Required: instruction and exactly one timing field (delay, executeAt, or when). Set repeatEvery to an ISO-8601 duration such as P1D or P7D to make it recurring.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (string instruction, string? delay, string? executeAt, string? when, string? timeZoneId, string? repeatEvery, bool notifyOnCompletion, CancellationToken token) =>
                Services.GetRequiredService<AgentEventService>().ScheduleAgentTaskToolAsync(Access, instruction, delay, executeAt, when, timeZoneId, repeatEvery, notifyOnCompletion, token)),
        Local(AgentToolKeys.ListScheduledJobs, "List scheduled jobs", "Scheduling",
            "List scheduled agent tasks and notifications for the current subject. Optionally filter by status or return jobs created before an ISO-8601 datetime.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: false,
            (Services, Access) => (string? status, string? before, CancellationToken token) =>
                Services.GetRequiredService<ScheduledJobManagementService>().ListToolAsync(Access, status, before, token)),
        Local(AgentToolKeys.GetScheduledJob, "Inspect scheduled job", "Scheduling",
            "Get one scheduled job, including its current state, outcome, and execution attempts.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: false,
            (Services, Access) => (Guid jobId, CancellationToken token) =>
                Services.GetRequiredService<ScheduledJobManagementService>().GetToolAsync(Access, jobId, token)),
        Local(AgentToolKeys.UpdateScheduledJob, "Update scheduled job", "Scheduling",
            "Update a pending scheduled job. Agent tasks accept instruction and notifyOnCompletion; notifications accept title and body. Optionally change timing using one of delay, executeAt, or when.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (Guid jobId, string? instruction, string? title, string? body, string? delay, string? executeAt,
                string? when, string? timeZoneId, bool? notifyOnCompletion, CancellationToken token) =>
                Services.GetRequiredService<ScheduledJobManagementService>().UpdateToolAsync(Access, jobId, instruction, title, body,
                    delay, executeAt, when, timeZoneId, notifyOnCompletion, token)),
        Local(AgentToolKeys.CancelScheduledJob, "Cancel scheduled job", "Scheduling",
            "Cancel a pending scheduled agent task or notification. Completed or active jobs cannot be cancelled.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (Guid jobId, CancellationToken token) =>
                Services.GetRequiredService<ScheduledJobManagementService>().CancelToolAsync(Access, jobId, token)),
        Local(AgentToolKeys.GetCurrentDateTime, "Current date and time", "Core",
            "Get the current date and time, optionally in a specific IANA or Windows timezone (e.g. 'America/Chicago' or 'Central Standard Time'). Call this before scheduling relative times like 'at noon today' or 'next Monday'.",
            OwnerDefault: true, CoachDefault: true, HasSideEffects: false,
            (Services, _) => Services.GetRequiredService<AgentEventService>().GetCurrentDateTimeToolAsync)
    ]);

    public IReadOnlyList<AgentToolRegistration> GetRegistrations()
    {
        var registrations = new List<AgentToolRegistration>(LocalTools);
        registrations.AddRange(TavilyProvider.GetTools().Select(Tool => new AgentToolRegistration(
            new AgentToolDescriptor(AgentToolKeys.Tavily(Tool.Name), Tool.Name, Tool.Name.Replace('_', ' '),
                "Tavily web search", Tool.Description, TavilyProvider.IsAvailable, false, false, HasSideEffects: true),
            "TavilyMcp", (_, _) => Tool)));
        if (registrations.DistinctBy(Tool => Tool.Descriptor.Key, StringComparer.Ordinal).Count() != registrations.Count
            || registrations.DistinctBy(Tool => Tool.Descriptor.Name, StringComparer.Ordinal).Count() != registrations.Count)
            throw new InvalidOperationException("Tool registrations must have unique keys and function names.");
        return registrations.AsReadOnly();
    }

    private static AgentToolRegistration Local(string Key, string DisplayName, string Integration, string Description,
        bool OwnerDefault, bool CoachDefault, bool HasSideEffects, Func<IServiceProvider, AgentAccessContext, Delegate> Handler)
    {
        var name = Key["Local:".Length..];
        return new AgentToolRegistration(
            new AgentToolDescriptor(Key, name, DisplayName, Integration, Description, true, OwnerDefault, CoachDefault, HasSideEffects),
            "Local", (Services, Access) => AIFunctionFactory.Create(Handler(Services, Access), name, Description));
    }
}
