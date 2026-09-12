using Microsoft.Extensions.AI;

using PersonalAgent.Models;

namespace PersonalAgent.Services;

// API composition boundary: metadata and the context-bound handler are declared together.
internal sealed class AgentToolRegistry(ITavilyMcpToolProvider TavilyProvider) : IAgentToolRegistry
{
    private static readonly IReadOnlyList<AgentToolRegistration> LocalTools = Array.AsReadOnly<AgentToolRegistration>(
    [
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
            "Schedule a mobile notification for the current user using delay, absolute executeAt datetime, or natural when text like 'tonight'.",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (string title, string body, string? delay, string? executeAt, string? when, string? timeZoneId, CancellationToken token) =>
                Services.GetRequiredService<AgentEventService>().ScheduleNotificationToolAsync(Access, title, body, delay, executeAt, when, timeZoneId, token)),
        Local(AgentToolKeys.ScheduleAgentTask, "Schedule agent task", "Scheduling",
            "Schedule a future agent task. Required: instruction and exactly one timing field (delay, executeAt, or when).",
            OwnerDefault: true, CoachDefault: false, HasSideEffects: true,
            (Services, Access) => (string instruction, string? delay, string? executeAt, string? when, string? timeZoneId, bool notifyOnCompletion, CancellationToken token) =>
                Services.GetRequiredService<AgentEventService>().ScheduleAgentTaskToolAsync(Access, instruction, delay, executeAt, when, timeZoneId, notifyOnCompletion, token)),
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
