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
    public Event<ReconcileAutomation> Reconcile { get; private set; } = null!;

    public AutomationStateMachine()
    {
        InstanceState(S => S.CurrentState);
        Event(() => Start, E => E.CorrelateById(C => C.Message.RunId));
        Event(() => StepCompleted, E => { E.CorrelateById(C => C.Message.RunId); E.OnMissingInstance(M => M.Discard()); });
        Event(() => StepFailed, E => { E.CorrelateById(C => C.Message.RunId); E.OnMissingInstance(M => M.Discard()); });
        Event(() => Reconcile, E => { E.CorrelateById(C => C.Message.RunId); E.OnMissingInstance(M => M.Discard()); });
        Initially(When(Start).ThenAsync(async C => await Handler(C).BeginAsync(C.Saga, C))
            .IfElse(C => C.Saga.Error is null, B => B.TransitionTo(Running), B => B.TransitionTo(Blocked)));
        During(Running,
            Ignore(Start),
            When(Reconcile).ThenAsync(async C => await Handler(C).ReconcileAsync(C.Saga, C))
                .If(C => C.Saga.Error is not null, B => B.TransitionTo(Blocked)),
            When(StepCompleted)
                .ThenAsync(async C => await Handler(C).CompleteStepAsync(C.Saga, C.Message, C))
                .IfElse(C => C.Saga.Error is not null, B => B.TransitionTo(Blocked),
                    B => B.IfElse(C => C.Saga.FinishedAt is not null, D => D.TransitionTo(Completed), D => D.TransitionTo(Running))),
            When(StepFailed)
                .ThenAsync(async C => await Handler(C).FailStepAsync(C.Saga, C.Message, C))
                .If(C => C.Saga.Error is not null, B => B.IfElse(C => C.Message.Blocked, D => D.TransitionTo(Blocked), D => D.TransitionTo(Failed))));
        During(Completed, Ignore(Start), Ignore(StepCompleted), Ignore(StepFailed), Ignore(Reconcile));
        During(Failed, Ignore(Start), Ignore(StepCompleted), Ignore(StepFailed), Ignore(Reconcile));
        During(Blocked, Ignore(Start), Ignore(StepCompleted), Ignore(StepFailed), Ignore(Reconcile));
    }

    private static AutomationSagaActions Handler<T>(BehaviorContext<AutomationRun, T> Context) where T : class =>
        Context.GetPayload<IServiceProvider>().GetRequiredService<AutomationSagaActions>();
}

internal sealed class AutomationSagaActions(AutomationDbContext Db, AutomationAuthorization Authorization,
    AutomationRecipes Recipes, ToolAccessService Permissions, TimeProvider Clock, ILogger<AutomationSagaActions> Logger, ScratchpadStore Scratchpads,
    IServiceProvider Services)
{
    public async Task BeginAsync(AutomationRun Run, ConsumeContext Context)
    {
        using var Span = AutomationTelemetry.Start("automation.start", Run.CorrelationId);
        // Only runs created by our scheduling transaction can execute.
        if (!await Db.Runs.AsNoTracking().AnyAsync(R => R.CorrelationId == Run.CorrelationId, Context.CancellationToken))
            throw new InvalidOperationException("No durable automation run exists.");
        Run.StartedAt = Clock.GetUtcNow();
        Run.TraceId = Activity.Current?.TraceId.ToString();
        try { await Authorization.ForRunAsync(await DefinitionAsync(Run, Context.CancellationToken), Context.CancellationToken,
            await Scratchpads.ScopeAsync(Run.CorrelationId, Context.CancellationToken) is not null); }
        catch (UnauthorizedAccessException) { await FailAsync(Run, "Automation access was revoked.", true, Context); return; }
        await DispatchAsync(Run, Context);
        Logger.LogInformation("Automation run {RunId} started at revision {Version}", Run.CorrelationId, Run.Version);
    }

    public async Task CompleteStepAsync(AutomationRun Run, AutomationStepCompleted Result, ConsumeContext Context)
    {
        using var Span = AutomationTelemetry.Start("automation.commit_step", Run.CorrelationId, Run.StepIndex);
        var Token = Context.CancellationToken;
        var Definition = await DefinitionAsync(Run, Token);
        var Version = await Db.Versions.SingleAsync(V => V.AutomationId == Run.AutomationId && V.Version == Run.Version, Token);
        var Recipe = Recipes.Parse(Version.Source);
        if (!await AcceptAsync(Run, Recipe, Result.StepIndex, Result.Attempt, Token)) return;
        AgentAccessContext Access;
        var Interactive = await Scratchpads.ScopeAsync(Run.CorrelationId, Token) is not null;
        try { Access = await Authorization.ForRunAsync(Definition, Token, Interactive); }
        catch (UnauthorizedAccessException) { await FailAsync(Run, "Automation access was revoked.", true, Context); return; }
        var Step = Recipe.Steps[Result.StepIndex];
        var Record = await Db.Steps.SingleAsync(S => S.RunId == Run.CorrelationId && S.Index == Result.StepIndex, Token);
        try { await Recipes.ValidateToolsAsync(Recipe with { Steps = [Step] }, Services, Access, Permissions, Token, Interactive); }
        catch (UnauthorizedAccessException) { await FailAsync(Run, "An action grant was revoked.", true, Context); return; }
        if (Step.Action is "public_research" or "synthesize")
        {
            try { await AutomationAuthorization.RequireWorkflowModelAsync(Services, Access, Recipe.SupervisedModelId, Token); }
            catch (UnauthorizedAccessException) { await FailAsync(Run, "The workflow model was revoked.", true, Context); return; }
        }
        if (Recipe.Supervised && !await Authorization.CanSuperviseAsync(Access, Token))
        { await FailAsync(Run, "Supervised workflow permission was revoked.", true, Context); return; }
        if (Recipe.Supervised && AutomationPrograms.IsProgram(Step.Action)
            && !await Permissions.IsAllowedAsync(Access.Role, AutomationPrograms.RunSandboxPermissionKey, Token))
        { await FailAsync(Run, "The one-time Railway sandbox permission was revoked.", true, Context); return; }
        if (!Interactive && AutomationPrograms.IsProgram(Step.Action) && (Access.Role != AgentRoles.Owner
            || !await Permissions.IsAllowedAsync(Access.Role, AutomationPrograms.PermissionKey, Token)))
        { await FailAsync(Run, "Sandbox program permission was revoked.", true, Context); return; }
        if (AutomationPrograms.IsProgram(Step.Action) && !Result.Skipped && (Result.Program is null || Result.Program.Status != "Completed"
            || Result.Program.SourceHash != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Step.Arguments.GetProperty("source").GetString()!)))))
            throw new InvalidOperationException("Sandbox result does not match the pinned source.");
        if (!Result.Skipped && Step.Action == "notify" && !await Permissions.IsAllowedAsync(Access.Role, AgentToolKeys.PublishMobileNotification, Token))
        { await FailAsync(Run, "Notification permission was revoked.", true, Context); return; }
        if (Result.Output.Length > 65536) throw new InvalidOperationException("Invalid oversized step result.");
        Record.Output = Result.Output;
        Record.ProgramEvidence = Result.Program is null ? null : JsonSerializer.Serialize(Result.Program);
        Record.Status = Result.Skipped ? "Skipped" : "Completed";
        Record.CompletedAt = Clock.GetUtcNow();
        if (!Result.Skipped && Step.Action is "save_report" or "notify")
        {
            // Derive effects from the pinned recipe and committed prior outputs, not arbitrary message fields.
            var Earlier = await Db.Steps.Where(S => S.RunId == Run.CorrelationId && S.Index < Result.StepIndex).ToListAsync(Token);
            var Arguments = AutomationRecipes.Resolve(Step, Run, Earlier);
            var Title = Arguments.GetProperty("title").GetString()!;
            if (Step.Action == "save_report") Db.Reports.Add(new() { Id = Guid.NewGuid(), RunId = Run.CorrelationId, StepIndex = Result.StepIndex,
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

    public async Task FailAsync(AutomationRun Run, string Error, bool Blocked, ConsumeContext Context, AutomationProgramEvidence? Program = null, int? FailedIndex = null)
    {
        var Token = Context.CancellationToken;
        Run.Error = Error;
        Run.FinishedAt = Clock.GetUtcNow();
        var Index = FailedIndex ?? Run.StepIndex;
        var Record = await Db.Steps.SingleOrDefaultAsync(S => S.RunId == Run.CorrelationId && S.Index == Index, Token);
        if (Record is not null && AutomationChildScheduling.Terminal(Record)) Record = null;
        if (Record is not null) { Record.Status = Blocked ? "Blocked" : "Failed"; Record.Error = Error; Record.CompletedAt = Run.FinishedAt;
            Record.ProgramEvidence = Program is null ? null : JsonSerializer.Serialize(Program); }
        (await DefinitionAsync(Run, Token)).Status = "Paused";
        if (await Db.Steps.AnyAsync(S => S.RunId == Run.CorrelationId && S.ChildId != null, Token))
        {
            var Remaining = await Db.Steps.Where(S => S.RunId == Run.CorrelationId && S.Status != "Completed" && S.Status != "Skipped").ToListAsync(Token);
            foreach (var Step in Remaining)
            {
                Step.Status = Step.Index == Index && !Blocked ? "Failed" : "Blocked";
                Step.Error = Error;
                Step.CompletedAt = Run.FinishedAt;
                if (Step.Action != "coding_job") continue;
                // Commit cancellation intent with the failed parent, then use the existing
                // coding store's cancellation/publication gate. Safe across either host restart.
                await (await Context.GetSendEndpoint(AutomationRegistration.StepAddress)).Send(new CancelAutomationChild(Run.CorrelationId, Step.Index), Token);
            }
        }
        AutomationTelemetry.Finished(Blocked ? "Blocked" : "Failed");
        Activity.Current?.SetStatus(ActivityStatusCode.Error);
        // Stable EventId + trace context flows to the existing metadata-only Sentry integration.
        Logger.LogError(new EventId(4301, "AutomationFailed"), "Automation run {RunId} ended with status {Status}", Run.CorrelationId, Blocked ? "Blocked" : "Failed");
    }

    private Task<AutomationDefinition> DefinitionAsync(AutomationRun Run, CancellationToken Token) => Db.Automations.SingleAsync(D => D.Id == Run.AutomationId, Token);
    private async Task<bool> AcceptAsync(AutomationRun Run, AutomationRecipe Recipe, int Index, int Attempt, CancellationToken Token)
    {
        if (Index < 0 || Index >= Recipe.Steps.Count) return false;
        if (!Recipe.ParallelChildren) return Index == Run.StepIndex;
        var Step = await Db.Steps.SingleAsync(S => S.RunId == Run.CorrelationId && S.Index == Index, Token);
        return (Step.Status is "Queued" or "Running") && (!AutomationChildScheduling.IsChild(Recipe.Steps[Index]) || Step.Attempt == Attempt);
    }

    public async Task FailStepAsync(AutomationRun Run, AutomationStepFailed Result, ConsumeContext Context)
    {
        var Version = await Db.Versions.SingleAsync(V => V.AutomationId == Run.AutomationId && V.Version == Run.Version, Context.CancellationToken);
        var Recipe = Recipes.Parse(Version.Source);
        if (!await AcceptAsync(Run, Recipe, Result.StepIndex, Result.Attempt, Context.CancellationToken)) return;
        await FailAsync(Run, Result.Error, Result.Blocked, Context, Result.Program, Result.StepIndex);
    }

    private async Task DispatchAsync(AutomationRun Run, ConsumeContext Context)
    {
        var Token = Context.CancellationToken;
        var Version = await Db.Versions.SingleAsync(V => V.AutomationId == Run.AutomationId && V.Version == Run.Version, Token);
        var Recipe = Recipes.Parse(Version.Source);
        var Endpoint = await Context.GetSendEndpoint(AutomationRegistration.StepAddress);
        if (!Recipe.ParallelChildren)
        { await Endpoint.Send(new ExecuteAutomationStep(Run.CorrelationId, Run.StepIndex), Token); return; }
        var Steps = await Db.Steps.Where(S => S.RunId == Run.CorrelationId).ToListAsync(Token);
        foreach (var Step in Steps.Where(S => S.Status == "Pending" && AutomationChildScheduling.Ready(Recipe, S.Index, Steps)))
        {
            Step.ChildId ??= Guid.Parse(AutomationStepConsumer.CodingRequestKey(Run.CorrelationId, Step.Index));
            Step.Attempt++;
            Step.Status = "Queued";
            Step.Deadline ??= Clock.GetUtcNow().AddMinutes(Step.Action == "dockerfile" ? 18 : 5);
            Step.LeaseUntil = AutomationChildScheduling.IsChild(Recipe.Steps[Step.Index]) ? Clock.GetUtcNow().AddSeconds(75) : Step.Deadline;
            await Endpoint.Send(new ExecuteAutomationStep(Run.CorrelationId, Step.Index, Step.Attempt), Token);
        }
    }

    public async Task ReconcileAsync(AutomationRun Run, ConsumeContext Context)
    {
        var Token = Context.CancellationToken;
        var Version = await Db.Versions.SingleAsync(V => V.AutomationId == Run.AutomationId && V.Version == Run.Version, Token);
        var Recipe = Recipes.Parse(Version.Source);
        if (!Recipe.ParallelChildren) return;
        // Claims commit outside the consumer outbox transaction. Lock the rows before reading
        // leases so recovery cannot overwrite a concurrent claim that has just renewed one.
        var Steps = await Db.Steps.FromSqlInterpolated($"SELECT * FROM automation.\"Steps\" WHERE \"RunId\" = {Run.CorrelationId} ORDER BY \"Index\" FOR UPDATE").ToListAsync(Token);
        var Definition = await DefinitionAsync(Run, Token);
        try
        {
            var Access = await Authorization.ForRunAsync(Definition, Token);
            if (!await Authorization.CanSuperviseAsync(Access, Token)) throw new UnauthorizedAccessException();
            await Recipes.ValidateToolsAsync(Recipe, Services, Access, Permissions, Token);
            if (Recipe.Steps.Any(S => S.Action is "public_research" or "synthesize"))
                await AutomationAuthorization.RequireWorkflowModelAsync(Services, Access, Recipe.SupervisedModelId, Token);
            if (Recipe.Steps.Any(S => AutomationPrograms.IsProgram(S.Action))
                && !await Permissions.IsAllowedAsync(Access.Role, AutomationPrograms.RunSandboxPermissionKey, Token))
                throw new UnauthorizedAccessException();
        }
        catch (UnauthorizedAccessException) { await FailAsync(Run, "Workflow access was revoked.", true, Context); return; }
        foreach (var Step in Steps.Where(S => S.Status is "Queued" or "Running"))
        {
            if (Step.Action == "coding_job")
            {
                var Job = await Services.GetRequiredService<CodingJobStore>().FindRequestAsync(Definition.ActorId,
                    AutomationStepConsumer.CodingRequestKey(Run.CorrelationId, Step.Index), Token);
                if (Job is not null && Job.Subject == Definition.SubjectProfileId)
                {
                    if (PersonalAgent.Contracts.Coding.CodingJobs.Terminal(Job.Status))
                        await (await Context.GetSendEndpoint(AutomationRegistration.SagaAddress)).Send(new AutomationStepCompleted(
                            Run.CorrelationId, Step.Index, JsonSerializer.Serialize(new { jobId = Job.Id, Job.Status, Job.PullRequestUrl, Job.Error }, AutomationRecipes.Json),
                            Attempt: Step.Attempt), Token);
                    else if (Job.Deadline.AddMinutes(5) < Clock.GetUtcNow())
                    { await FailAsync(Run, "Coding child progress deadline expired.", true, Context, FailedIndex: Step.Index); return; }
                    continue; // Never restart a durable coding job or reserve a new provider budget.
                }
            }
            if (Step.LeaseUntil > Clock.GetUtcNow()) continue;
            if (Step.Attempt >= 3 || Step.Deadline <= Clock.GetUtcNow() || !AutomationChildScheduling.IsChild(Recipe.Steps[Step.Index]))
            { await FailAsync(Run, "Child progress deadline or attempt budget expired.", true, Context, FailedIndex: Step.Index); return; }
            Step.Status = "Pending";
        }
        await DispatchAsync(Run, Context);
    }
}
