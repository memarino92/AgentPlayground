using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationAuthorization(ScheduledJobAuthorization Subjects, ToolAccessService Tools)
{
    public const string ManageKey = "Local:save_automation";
    public const string SuperviseKey = "Local:run_supervised_workflow";

    public Task<bool> CanSuperviseAsync(AgentAccessContext Access, CancellationToken Token) =>
        Access.Role == AgentRoles.Owner ? Tools.IsAllowedAsync(Access.Role, SuperviseKey, Token) : Task.FromResult(false);

    public static async Task<string> RequireWorkflowModelAsync(IServiceProvider Services, AgentAccessContext Access,
        string? ModelId, CancellationToken Token)
    {
        if (Access.Role != AgentRoles.Owner || string.IsNullOrWhiteSpace(ModelId))
            throw new UnauthorizedAccessException("This workflow needs an available chat model selected by the owner.");
        if (ModelId.StartsWith("chatgpt:", StringComparison.Ordinal))
        {
            var Catalog = Services.GetRequiredService<ChatGptModelCatalog>();
            return (await Catalog.GetModelsAsync(Access.ActorId, Token)).FirstOrDefault(Model => Model.Id == ModelId)?.Id
                ?? throw new UnauthorizedAccessException("The workflow's selected ChatGPT model is no longer available to this owner.");
        }
        var Model = await Services.GetRequiredService<IChatModelCatalog>().FindModelAsync(ModelId, Token);
        return Model?.Id ?? throw new UnauthorizedAccessException("The workflow's selected chat model is no longer available.");
    }
    public async Task<AgentAccessContext> RequireAsync(AgentAccessContext Claimed, CancellationToken Token, bool Write = true)
    {
        var Current = await Subjects.ResolveAsync(Claimed.ActorId, Claimed.Email, Claimed.SubjectProfileId, Token);
        if (Current is null || Current.Role != Claimed.Role || (Write && !await Tools.IsAllowedAsync(Current.Role, ManageKey, Token)))
            throw new UnauthorizedAccessException("Current automation and subject access is required.");
        return Current with { SessionId = Claimed.SessionId };
    }

    public async Task<AgentAccessContext> ForRunAsync(AutomationDefinition Definition, CancellationToken Token, bool Scratch = false)
    {
        var Current = await Subjects.ResolveAsync(Definition.ActorId, Definition.ActorEmail, Definition.SubjectProfileId, Token);
        if (Current is null || (Scratch && Current.Role != AgentRoles.Owner)
            || !await Tools.IsAllowedAsync(Current.Role, Scratch ? PersonalAgent.Contracts.Automations.AutomationPrograms.ScratchPermission : ManageKey, Token))
            throw new UnauthorizedAccessException("Automation access has been revoked.");
        return Current;
    }

    public static bool Owns(AgentAccessContext Access, AutomationDefinition Definition) =>
        Access.SubjectProfileId == Definition.SubjectProfileId && (Access.Role == AgentRoles.Owner || Access.ActorId == Definition.ActorId);
}
