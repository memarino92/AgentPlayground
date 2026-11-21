using Microsoft.Extensions.Caching.Memory;

using PersonalAgent.Api.Models;
using PersonalAgent.Api.Security;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Services;

// Proposals are not approvals. Only the authenticated review endpoint can commit one.
internal sealed class SiteSettingsActions(IDatabaseSettingsStore Store, IConfiguration Configuration, ILogger<SiteSettingsActions>? Logger = null) : IDisposable
{
    private readonly MemoryCache Proposals = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private sealed record Pending(string Actor, CoachingSettingProposal Proposal, DatabaseSettingEdit Edit);

    internal static bool IsCoachingName(string Scope, string Key) => Scope is "Shared" or "Api"
        && Key is "CoachCheckins:CoachName" or "CoachCheckins:AthleteName";

    internal static void ValidateNames(SaveDatabaseSettingsRequest Request)
    {
        foreach (var Edit in Request.Changes ?? [])
            if (Edit is not null && IsCoachingName(Edit.Scope, Edit.Key) && Edit.Value is { } Value
                && (string.IsNullOrWhiteSpace(Value) || Value.Length > 100 || Value.Any(char.IsControl)))
                throw new IntegrationValidationException(new() { [Edit.Key] = ["Participant names must be 1–100 characters without control characters."] });
    }

    public Task SaveManualAsync(SaveDatabaseSettingsRequest Request, CancellationToken Token)
    {
        JevRoutingRuntime.ValidateEdits(Request);
        ValidateNames(Request);
        return Store.SaveAsync(Request, Token);
    }

    private void Authorize(AgentAccessContext Access)
    {
        if (!IntegrationAdministratorFilter.IsAllowed(Configuration, Access.ActorId, Access.Role)
            || !string.Equals(Access.ActorId, Access.SubjectProfileId, StringComparison.OrdinalIgnoreCase)
            || Access.ScheduledTaskId is not null)
            throw new UnauthorizedAccessException("Current-user deployment administrator access is required.");
    }

    public async Task<object> FindAsync(AgentAccessContext Access, string Key, string? Scope, CancellationToken Token)
    {
        Authorize(Access);
        // Exact keys only. Return metadata and field links, never operational values or secrets.
        if (Key.StartsWith("input:", StringComparison.Ordinal)) return ManualSettingInputs.Find(Key, Scope).ToArray();
        var Rows = (await Store.ReadAsync(Token)).Where(Row => (Key.Length == 0 || Row.Key == Key) && (Scope is null || Row.Scope == Scope));
        return Rows.Select(Row => new SettingInputLink(Row.Scope, Row.Key, Row.IsActive && !Row.IsSecret && IsCoachingName(Row.Scope, Row.Key),
            FieldLink(Row.Scope, Row.Key) + (Row.IsSecret ? "-action" : ""))).Concat(ManualSettingInputs.Find(Key, Scope)).ToArray();
    }

    internal static string FieldId(string Scope, string Key) => "setting-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(Scope + ":" + Key));
    internal static string FieldLink(string Scope, string Key) => $"/admin/settings?section=database&scope={Uri.EscapeDataString(Scope)}&key={Uri.EscapeDataString(Key)}#{FieldId(Scope, Key)}";

    public async Task<CoachingSettingProposal> ProposeAsync(AgentAccessContext Access, string Scope, string Key, string Value, CancellationToken Token)
    {
        Authorize(Access);
        if (!IsCoachingName(Scope, Key)) throw new IntegrationValidationException(new() { ["Key"] = ["This setting is manual-only. Use find_setting_input for its exact input link."] });
        if (Value is null) throw new IntegrationValidationException(new() { ["Value"] = ["An exact participant name is required."] });
        var Row = (await Store.ReadAsync(Token)).SingleOrDefault(Row => Row.Scope == Scope && Row.Key == Key);
        if (Row is null || Row.IsSecret || !Row.IsActive) throw new IntegrationConflictException();
        var Edit = new DatabaseSettingEdit(Scope, Key, Row.Version, Value, Row.IsActive);
        ValidateNames(new([Edit]));
        var Id = Guid.NewGuid();
        var Proposal = new CoachingSettingProposal(Id, Scope, Key, Row.Value, Value, $"/admin/settings?section=database&proposal={Id}");
        Proposals.Set(Id, new Pending(Access.ActorId, Proposal, Edit), new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15) });
        return Proposal;
    }

    private Pending Get(AgentAccessContext Access, Guid Id)
    {
        Authorize(Access);
        if (!Proposals.TryGetValue<Pending>(Id, out var Pending) || Pending is null
            || !string.Equals(Pending.Actor, Access.ActorId, StringComparison.OrdinalIgnoreCase)) throw new IntegrationConflictException();
        return Pending;
    }

    public CoachingSettingProposal Review(AgentAccessContext Access, Guid Id) => Get(Access, Id).Proposal;

    public async Task ConfirmAsync(AgentAccessContext Access, Guid Id, bool Confirm, CancellationToken Token)
    {
        var Pending = Get(Access, Id);
        if (!Confirm) throw new IntegrationValidationException(new() { ["Confirm"] = ["Review and explicitly confirm the exact proposed change."] });
        var Row = (await Store.ReadAsync(Token)).SingleOrDefault(Row => Row.Scope == Pending.Edit.Scope && Row.Key == Pending.Edit.Key);
        if (Row is null || Row.IsSecret || Row.Version != Pending.Edit.Version || !Row.IsActive) throw new IntegrationConflictException();
        // Row-version concurrency in the shared store also prevents concurrent confirmations/replays.
        await SaveManualAsync(new([Pending.Edit]), Token);
        Proposals.Remove(Id);
        Logger?.LogInformation("Confirmed coaching settings proposal {ProposalId} for actor {ActorId}, scope {Scope}, key {Key}", Id, Access.ActorId, Pending.Edit.Scope, Pending.Edit.Key);
    }

    public void Dispose() => Proposals.Dispose();
}
