using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationService(AutomationDbContext Db, AutomationAuthorization Authorization,
    AutomationRecipes Recipes, ToolAccessService Permissions, IServiceProvider Services, TimeProvider Clock)
{
    public async Task<AutomationSummary> SaveAsync(AgentAccessContext Access, Guid? Id, SaveAutomationRequest Request, CancellationToken Token)
    {
        Access = await Authorization.RequireAsync(Access, Token);
        if (string.IsNullOrWhiteSpace(Request.Name) || Request.Name.Length > 160) throw new ArgumentException("Name must contain 1–160 characters.");
        var Recipe = Recipes.Parse(Request.Source);
        await Recipes.ValidateToolsAsync(Recipe, Services, Access, Permissions, Token);
        TimeSpan? Interval;
        try { Interval = SchedulingTimeParser.ParseRecurrence(Request.RepeatEvery); }
        catch (InvalidOperationException E) { throw new ArgumentException(E.Message); }
        var Now = Clock.GetUtcNow();
        await using var Transaction = await Db.Database.BeginTransactionAsync(Token);
        AutomationDefinition Definition;
        if (Id is { } Existing)
        {
            Definition = await LockedAsync(Existing, Token) ?? throw new KeyNotFoundException();
            if (!AutomationAuthorization.Owns(Access, Definition)) throw new KeyNotFoundException();
            // Only the originating actor may replace the executable source of a coach-owned automation.
            if (Definition.ActorId != Access.ActorId) throw new UnauthorizedAccessException("Only the author may revise this automation.");
            if (Request.ExpectedVersion != Definition.Version) throw new InvalidOperationException("Version changed. Inspect the current revision before updating.");
            Definition.Version++;
        }
        else
        {
            Definition = new() { Id = Guid.NewGuid(), ActorId = Access.ActorId, ActorEmail = Access.Email,
                SubjectProfileId = Access.SubjectProfileId, SourceSessionId = Access.SessionId, Version = 1, CreatedAt = Now };
            Db.Automations.Add(Definition);
        }
        Definition.Name = Request.Name.Trim();
        Definition.Status = "Active";
        Definition.Interval = Interval;
        Definition.NextRunAt = (Request.ExecuteAt ?? Now).ToUniversalTime();
        Db.Versions.Add(new() { AutomationId = Definition.Id, Version = Definition.Version, Source = Request.Source,
            Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Request.Source))), CreatedAt = Now });
        await Db.SaveChangesAsync(Token);
        await Transaction.CommitAsync(Token);
        return Summary(Definition);
    }

    public async Task<IReadOnlyList<AutomationSummary>> ListAsync(AgentAccessContext Access, int Offset, CancellationToken Token)
    {
        Access = await Authorization.RequireAsync(Access, Token, false);
        return (await Visible(Access).AsNoTracking().OrderByDescending(D => D.CreatedAt).ThenBy(D => D.Id)
            .Skip(Math.Clamp(Offset, 0, 100000)).Take(50).ToListAsync(Token)).Select(Summary).ToArray();
    }

    public async Task<AutomationDetail> DetailAsync(AgentAccessContext Access, Guid Id, int RunOffset, CancellationToken Token)
    {
        var Definition = await AccessibleAsync(Access, Id, Token);
        var Versions = await Db.Versions.AsNoTracking().Where(V => V.AutomationId == Id).OrderByDescending(V => V.Version)
            .Select(V => new AutomationVersionResponse(V.Version, V.Source, V.Hash, V.CreatedAt)).ToListAsync(Token);
        var Runs = await Db.Runs.AsNoTracking().Where(R => R.AutomationId == Id).OrderByDescending(R => R.ScheduledAt).ThenBy(R => R.CorrelationId)
            .Skip(Math.Clamp(RunOffset, 0, 100000)).Take(50).ToListAsync(Token);
        var Upcoming = new List<DateTimeOffset>();
        if (Definition.Status == "Active" && Definition.NextRunAt is { } Next)
        {
            Upcoming.Add(Next);
            if (Definition.Interval is { } Interval) for (var I = 1; I < 5; I++) Upcoming.Add(Next + Interval * I);
        }
        return new(Summary(Definition), Versions, Runs.Select(RunResponse).ToArray(), Upcoming);
    }

    public async Task<AutomationRunDetail> RunDetailAsync(AgentAccessContext Access, Guid Id, Guid RunId, CancellationToken Token)
    {
        await AccessibleAsync(Access, Id, Token);
        var Run = await Db.Runs.AsNoTracking().SingleOrDefaultAsync(R => R.CorrelationId == RunId && R.AutomationId == Id, Token) ?? throw new KeyNotFoundException();
        var Steps = await Db.Steps.AsNoTracking().Where(S => S.RunId == RunId).OrderBy(S => S.Index)
            .Select(S => new AutomationStepResponse(S.Index, S.StepId, S.Action, S.Status, S.Output, S.Error, S.CompletedAt, S.ProgramEvidence)).ToListAsync(Token);
        var Reports = await Db.Reports.AsNoTracking().Where(R => R.RunId == RunId).OrderBy(R => R.StepIndex)
            .Select(R => new AutomationReportResponse(R.Id, R.Title, R.Content, R.CreatedAt)).ToListAsync(Token);
        return new(RunResponse(Run), Steps, Reports);
    }

    public async Task SetStatusAsync(AgentAccessContext Access, Guid Id, bool Paused, CancellationToken Token)
    {
        Access = await Authorization.RequireAsync(Access, Token);
        await using var Transaction = await Db.Database.BeginTransactionAsync(Token);
        var Definition = await LockedAsync(Id, Token) ?? throw new KeyNotFoundException();
        if (!AutomationAuthorization.Owns(Access, Definition)) throw new KeyNotFoundException();
        Definition.Status = Paused ? "Paused" : "Active";
        if (!Paused && Definition.NextRunAt is null) Definition.NextRunAt = Clock.GetUtcNow();
        await Db.SaveChangesAsync(Token);
        await Transaction.CommitAsync(Token);
    }

    public async Task<Guid?> StartAsync(Guid Id, AgentAccessContext? ManualAccess, CancellationToken Token)
    {
        if (ManualAccess is not null) await Authorization.RequireAsync(ManualAccess, Token);
        await using var Transaction = await Db.Database.BeginTransactionAsync(Token);
        var Definition = await LockedAsync(Id, Token) ?? throw new KeyNotFoundException();
        if (ManualAccess is not null && !AutomationAuthorization.Owns(ManualAccess, Definition)) throw new KeyNotFoundException();
        var Now = Clock.GetUtcNow();
        if (ManualAccess is null && (Definition.Status != "Active" || Definition.NextRunAt is null || Definition.NextRunAt > Now)) return null;
        if (await Db.Runs.AnyAsync(R => R.AutomationId == Id && (R.CurrentState == "Initial" || R.CurrentState == "Running"), Token))
        {
            if (ManualAccess is not null) throw new InvalidOperationException("An execution is already active.");
            return null;
        }
        var Version = await Db.Versions.SingleAsync(V => V.AutomationId == Id && V.Version == Definition.Version, Token);
        var Recipe = Recipes.Parse(Version.Source);
        var Run = new AutomationRun { CorrelationId = Guid.NewGuid(), AutomationId = Id, Version = Definition.Version,
            ScheduledAt = ManualAccess is null ? Definition.NextRunAt!.Value : Now };
        Db.Runs.Add(Run);
        for (var I = 0; I < Recipe.Steps.Count; I++) Db.Steps.Add(new() { RunId = Run.CorrelationId, Index = I, StepId = Recipe.Steps[I].Id, Action = Recipe.Steps[I].Action });
        if (ManualAccess is null) Definition.NextRunAt = Definition.Interval is { } Interval ? NextOccurrence(Run.ScheduledAt, Interval, Now) : null;
        // Send through the scoped bus outbox; it shares this context and transaction.
        var Sender = Services.GetRequiredService<ISendEndpointProvider>();
        await (await Sender.GetSendEndpoint(AutomationRegistration.SagaAddress)).Send(new StartAutomation(Run.CorrelationId), Token);
        await Db.SaveChangesAsync(Token);
        await Transaction.CommitAsync(Token);
        return Run.CorrelationId;
    }

    private async Task<AutomationDefinition> AccessibleAsync(AgentAccessContext Access, Guid Id, CancellationToken Token)
    {
        Access = await Authorization.RequireAsync(Access, Token, false);
        return await Visible(Access).AsNoTracking().SingleOrDefaultAsync(D => D.Id == Id, Token) ?? throw new KeyNotFoundException();
    }
    private IQueryable<AutomationDefinition> Visible(AgentAccessContext Access) => Db.Automations.Where(D =>
        D.SubjectProfileId == Access.SubjectProfileId && (Access.Role == AgentRoles.Owner || D.ActorId == Access.ActorId));
    private Task<AutomationDefinition?> LockedAsync(Guid Id, CancellationToken Token) => Db.Automations
        .FromSqlInterpolated($"SELECT * FROM automation.\"Definitions\" WHERE \"Id\" = {Id} FOR UPDATE").SingleOrDefaultAsync(Token);
    internal static DateTimeOffset NextOccurrence(DateTimeOffset Due, TimeSpan Interval, DateTimeOffset Now) =>
        Due.AddTicks(checked((Math.Max(0, (Now - Due).Ticks / Interval.Ticks) + 1) * Interval.Ticks));
    internal static AutomationSummary Summary(AutomationDefinition D) => new(D.Id, D.Name, D.Status, D.Version, D.CreatedAt, D.NextRunAt, D.Interval, D.SubjectProfileId, D.ActorId);
    internal static AutomationRunResponse RunResponse(AutomationRun R) => new(R.CorrelationId, R.Version, R.CurrentState == "Initial" ? "Queued" : R.CurrentState,
        R.ScheduledAt, R.StartedAt, R.FinishedAt, R.Error, R.TraceId);
}
