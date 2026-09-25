using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Contracts.Messaging.Events;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationStateMachine : MassTransitStateMachine<AutomationRun>
{
    public State Running { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Failed { get; private set; } = null!;
    public State Blocked { get; private set; } = null!;
    public Event<StartAutomation> Start { get; private set; } = null!;
    public Event<AutomationStepCompleted> StepCompleted { get; private set; } = null!;
    public Event<AutomationStepFailed> StepFailed { get; private set; } = null!;

    public AutomationStateMachine()
    {
        InstanceState(S => S.CurrentState);
        Event(() => Start, E => E.CorrelateById(C => C.Message.RunId));
        Event(() => StepCompleted, E => { E.CorrelateById(C => C.Message.RunId); E.OnMissingInstance(M => M.Discard()); });
        Event(() => StepFailed, E => { E.CorrelateById(C => C.Message.RunId); E.OnMissingInstance(M => M.Discard()); });
        Initially(When(Start).ThenAsync(async C => await Handler(C).BeginAsync(C.Saga, C))
            .IfElse(C => C.Saga.Error is null, B => B.TransitionTo(Running), B => B.TransitionTo(Blocked)));
        During(Running,
            Ignore(Start),
            When(StepCompleted, C => C.Message.StepIndex == C.Saga.StepIndex)
                .ThenAsync(async C => await Handler(C).CompleteStepAsync(C.Saga, C.Message, C))
                .IfElse(C => C.Saga.Error is not null, B => B.TransitionTo(Blocked),
                    B => B.IfElse(C => C.Saga.FinishedAt is not null, D => D.TransitionTo(Completed), D => D.TransitionTo(Running))),
            When(StepFailed, C => C.Message.StepIndex == C.Saga.StepIndex)
                .ThenAsync(async C => await Handler(C).FailAsync(C.Saga, C.Message.Error, C.Message.Blocked, C.CancellationToken, C.Message.Program))
                .IfElse(C => C.Message.Blocked, B => B.TransitionTo(Blocked), B => B.TransitionTo(Failed)));
        During(Completed, Ignore(Start), Ignore(StepCompleted), Ignore(StepFailed));
        During(Failed, Ignore(Start), Ignore(StepCompleted), Ignore(StepFailed));
        During(Blocked, Ignore(Start), Ignore(StepCompleted), Ignore(StepFailed));
    }

    private static AutomationSagaActions Handler<T>(BehaviorContext<AutomationRun, T> Context) where T : class =>
        Context.GetPayload<IServiceProvider>().GetRequiredService<AutomationSagaActions>();
}

internal sealed class AutomationSagaActions(AutomationDbContext Db, AutomationAuthorization Authorization,
    AutomationRecipes Recipes, ToolAccessService Permissions, TimeProvider Clock, ILogger<AutomationSagaActions> Logger)
{
    public async Task BeginAsync(AutomationRun Run, ConsumeContext Context)
    {
        using var Span = AutomationTelemetry.Start("automation.start", Run.CorrelationId);
        // Only runs created by our scheduling transaction can execute.
        if (!await Db.Runs.AsNoTracking().AnyAsync(R => R.CorrelationId == Run.CorrelationId, Context.CancellationToken))
            throw new InvalidOperationException("No durable automation run exists.");
        Run.StartedAt = Clock.GetUtcNow();
        Run.TraceId = Activity.Current?.TraceId.ToString();
        try { await Authorization.ForRunAsync(await DefinitionAsync(Run, Context.CancellationToken), Context.CancellationToken); }
        catch (UnauthorizedAccessException) { await FailAsync(Run, "Automation access was revoked.", true, Context.CancellationToken); return; }
        await DispatchAsync(Run, Context);
        Logger.LogInformation("Automation run {RunId} started at revision {Version}", Run.CorrelationId, Run.Version);
    }

    public async Task CompleteStepAsync(AutomationRun Run, AutomationStepCompleted Result, ConsumeContext Context)
    {
        using var Span = AutomationTelemetry.Start("automation.commit_step", Run.CorrelationId, Run.StepIndex);
        var Token = Context.CancellationToken;
        var Definition = await DefinitionAsync(Run, Token);
        AgentAccessContext Access;
        try { Access = await Authorization.ForRunAsync(Definition, Token); }
        catch (UnauthorizedAccessException) { await FailAsync(Run, "Automation access was revoked.", true, Token); return; }
        var Version = await Db.Versions.SingleAsync(V => V.AutomationId == Run.AutomationId && V.Version == Run.Version, Token);
        var Recipe = Recipes.Parse(Version.Source);
        var Step = Recipe.Steps[Run.StepIndex];
        var Record = await Db.Steps.SingleAsync(S => S.RunId == Run.CorrelationId && S.Index == Run.StepIndex, Token);
        if (Step.Action == "csharp" && (Access.Role != AgentRoles.Owner
            || !await Permissions.IsAllowedAsync(Access.Role, AutomationPrograms.PermissionKey, Token)))
        { await FailAsync(Run, "C# automation permission was revoked.", true, Token); return; }
        if (Step.Action == "csharp" && !Result.Skipped && (Result.Program is null || Result.Program.Status != "Completed"
            || Result.Program.SourceHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Step.Arguments.GetProperty("source").GetString()!)))))
            throw new InvalidOperationException("C# result does not match the pinned source.");
        if (!Result.Skipped && Step.Action == "notify" && !await Permissions.IsAllowedAsync(Access.Role, AgentToolKeys.PublishMobileNotification, Token))
        { await FailAsync(Run, "Notification permission was revoked.", true, Token); return; }
        if (Result.Output.Length > 65536) throw new InvalidOperationException("Invalid oversized step result.");
        Record.Output = Result.Output;
        Record.ProgramEvidence = Result.Program is null ? null : JsonSerializer.Serialize(Result.Program);
        Record.Status = Result.Skipped ? "Skipped" : "Completed";
        Record.CompletedAt = Clock.GetUtcNow();
        if (!Result.Skipped && Step.Action is "save_report" or "notify")
        {
            // Derive effects from the pinned recipe and committed prior outputs, not arbitrary message fields.
            var Earlier = await Db.Steps.Where(S => S.RunId == Run.CorrelationId && S.Index < Run.StepIndex).ToListAsync(Token);
            var Arguments = AutomationRecipes.Resolve(Step, Run, Earlier);
            var Title = Arguments.GetProperty("title").GetString()!;
            if (Step.Action == "save_report") Db.Reports.Add(new() { Id = Guid.NewGuid(), RunId = Run.CorrelationId, StepIndex = Run.StepIndex,
                Title = Title, Content = Arguments.GetProperty("content").GetString()!, CreatedAt = Clock.GetUtcNow() });
            else await Context.Publish(new DevicePushNotificationRequested
            {
                NotificationId = Guid.NewGuid(), RequestedAt = Clock.GetUtcNow(), ProfileId = Definition.SubjectProfileId,
                NotificationType = "automation", Title = Title, Body = Arguments.GetProperty("body").GetString()!,
                Data = new Dictionary<string, string> { ["source"] = "automation", ["automationId"] = Definition.Id.ToString(), ["runId"] = Run.CorrelationId.ToString() }
            }, Token);
        }
        Run.StepIndex++;
        if (Run.StepIndex == Recipe.Steps.Count)
        {
            Run.FinishedAt = Clock.GetUtcNow();
            AutomationTelemetry.Finished("Completed");
            Span?.SetTag("automation.status", "Completed");
            Logger.LogInformation("Automation run {RunId} completed with {StepCount} steps", Run.CorrelationId, Run.StepIndex);
        }
        else await DispatchAsync(Run, Context);
        // MassTransit's EF consumer outbox saves domain rows, saga state and messages in this transaction.
    }

    public async Task FailAsync(AutomationRun Run, string Error, bool Blocked, CancellationToken Token, AutomationProgramEvidence? Program = null)
    {
        Run.Error = Error;
        Run.FinishedAt = Clock.GetUtcNow();
        var Record = await Db.Steps.SingleOrDefaultAsync(S => S.RunId == Run.CorrelationId && S.Index == Run.StepIndex, Token);
        if (Record is not null) { Record.Status = Blocked ? "Blocked" : "Failed"; Record.Error = Error; Record.CompletedAt = Run.FinishedAt;
            Record.ProgramEvidence = Program is null ? null : JsonSerializer.Serialize(Program); }
        (await DefinitionAsync(Run, Token)).Status = "Paused";
        AutomationTelemetry.Finished(Blocked ? "Blocked" : "Failed");
        Activity.Current?.SetStatus(ActivityStatusCode.Error);
        // Stable EventId + trace context flows to the existing metadata-only Sentry integration.
        Logger.LogError(new EventId(4301, "AutomationFailed"), "Automation run {RunId} ended with status {Status}", Run.CorrelationId, Blocked ? "Blocked" : "Failed");
    }

    private Task<AutomationDefinition> DefinitionAsync(AutomationRun Run, CancellationToken Token) => Db.Automations.SingleAsync(D => D.Id == Run.AutomationId, Token);
    private static async Task DispatchAsync(AutomationRun Run, ConsumeContext Context) =>
        await (await Context.GetSendEndpoint(AutomationRegistration.StepAddress)).Send(new ExecuteAutomationStep(Run.CorrelationId, Run.StepIndex), Context.CancellationToken);
}
