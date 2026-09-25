using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationAuthorization(ScheduledJobAuthorization Subjects, ToolAccessService Tools)
{
    public const string ManageKey = "Local:save_automation";
    public async Task<AgentAccessContext> RequireAsync(AgentAccessContext Claimed, CancellationToken Token, bool Write = true)
    {
        var Current = await Subjects.ResolveAsync(Claimed.ActorId, Claimed.Email, Claimed.SubjectProfileId, Token);
        if (Current is null || Current.Role != Claimed.Role || (Write && !await Tools.IsAllowedAsync(Current.Role, ManageKey, Token)))
            throw new UnauthorizedAccessException("Current automation and subject access is required.");
        return Current with { SessionId = Claimed.SessionId };
    }

    public async Task<AgentAccessContext> ForRunAsync(AutomationDefinition Definition, CancellationToken Token)
    {
        var Current = await Subjects.ResolveAsync(Definition.ActorId, Definition.ActorEmail, Definition.SubjectProfileId, Token);
        if (Current is null || !await Tools.IsAllowedAsync(Current.Role, ManageKey, Token))
            throw new UnauthorizedAccessException("Automation access has been revoked.");
        return Current;
    }

    public static bool Owns(AgentAccessContext Access, AutomationDefinition Definition) =>
        Access.SubjectProfileId == Definition.SubjectProfileId && (Access.Role == AgentRoles.Owner || Access.ActorId == Definition.ActorId);
}
