using System.Text.Json;

using MassTransit;
using Npgsql;

using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed partial class CodingJobService(CodingJobStore Store, ScheduledJobAuthorization Subjects, ToolAccessService Tools, ICodingPublisher Publisher,
    ChatGptModelCatalog Models)
{
    public async Task RequireAsync(string Actor, string? Email, string Subject, CancellationToken Token)
    {
        var Current = await Subjects.ResolveAsync(Actor, Email, Subject, Token);
        if (Current?.Role != AgentRoles.Owner || !await Tools.IsAllowedAsync(Current.Role, CodingJobs.Permission, Token))
            throw new UnauthorizedAccessException("Current owner access and coding-job permission are required.");
    }

    public async Task<CodingJobView> StartAsync(AgentAccessContext Access, StartCodingJob Request, CancellationToken Token)
    {
        if (Access.Role != AgentRoles.Owner) throw new UnauthorizedAccessException();
        await RequireAsync(Access.ActorId, Access.Email, Access.SubjectProfileId, Token);
        if (string.IsNullOrWhiteSpace(Request.Instruction) || Request.Instruction.Length > 8000 || !Guid.TryParse(Request.RequestKey, out var RequestId))
            throw new ArgumentException("An instruction of 1–8000 characters and a stable UUID requestKey are required.");
        if (Request.WorkflowEvidence is { Length: > 12000 } || Request is { PreviousJobId: not null, WorkflowEvidence: not null })
            throw new ArgumentException("Workflow evidence must be bounded and apply to a new coding job.");
        var Key = RequestId.ToString("D");
        if (await Store.FindRequestAsync(Access.ActorId, Key, Token) is { } Existing) return CheckRequest(Existing);
        var Settings = (await Store.SettingsAsync(Token)).View.Settings;
        if (!Settings.Enabled) throw new InvalidOperationException("Configure and enable platform coding in Settings first.");
        if (CodingJobs.IsSubscriptionModel(Settings.ModelId)
            && !(await Models.GetModelsAsync(Access.ActorId, Token)).Any(Model => Model.Id == Settings.ModelId))
            throw new InvalidOperationException("The selected ChatGPT coding model is unavailable to this owner. Connect your account and select a model in Platform coding settings.");
        var Revision = await PrepareRevisionAsync(Access, Request, Settings, Token);
        var BaseSha = Revision?.Target.RebaseBase ?? Revision?.Target.Head ?? await Publisher.PrepareAsync(Settings, Token);
        var Id = Guid.NewGuid(); var Now = DateTimeOffset.UtcNow;
        var Job = await Store.CreateAsync(new()
        {
            Id = Id, ActorId = Access.ActorId, Email = Access.Email, Subject = Access.SubjectProfileId,
            Instruction = Request.Instruction.Trim(), AllowNoChanges = Request.AllowNoChanges, Settings = Settings, BaseSha = BaseSha,
            Supervise = Request.Supervise, AttemptLimit = CodingJobs.MaxAttempts,
            Branch = Revision?.Parent.Branch ?? $"feat/platform-improvement-{Id:N}",
            SessionId = Revision?.Parent.SessionId ?? ConversationId(Access), PreviousJobId = Request.PreviousJobId,
            PullRequestUrl = Revision?.Parent.PullRequestUrl,
            RootJobId = Revision is { } FollowUpTarget ? FollowUpTarget.Parent.RootJobId ?? FollowUpTarget.Parent.Id : null,
            FollowUp = Request.FollowUp, FollowUpEvidence = Revision?.Target.Evidence ?? Request.WorkflowEvidence, PullRequestNumber = Revision?.Target.Number,
            ExpectedBranchHead = Request.FollowUp == "rebase" ? Revision?.Target.Head : null,
            CreatedAt = Now, Deadline = Now.AddMinutes((Settings.MaxMinutes + 5) * CodingJobs.MaxAttempts)
        }, Key, Token);
        return CheckRequest(Job);

        CodingJobView CheckRequest(CodingJob Saved)
        {
            if (Saved.Instruction != Request.Instruction.Trim() || Saved.PreviousJobId != Request.PreviousJobId
                || Saved.FollowUp != Request.FollowUp || Saved.AllowNoChanges != Request.AllowNoChanges
                || Request.PreviousJobId is null && Saved.FollowUpEvidence != Request.WorkflowEvidence
                || Saved.Supervise != Request.Supervise || Saved.Subject != Access.SubjectProfileId)
                throw new InvalidOperationException("requestKey already identifies a different request.");
            return Saved.View;
        }
    }

    public async Task<IReadOnlyList<CodingJobView>> ListAsync(AgentAccessContext Access, CancellationToken Token)
    {
        await RequireAsync(Access.ActorId, Access.Email, Access.SubjectProfileId, Token);
        return (await Store.ListAsync(Access.ActorId, Access.SubjectProfileId, Token)).Select(J => J.View).ToArray();
    }

    public async Task<CodingJobView> InspectAsync(AgentAccessContext Access, Guid Id, bool Cancel, CancellationToken Token)
    {
        await RequireAsync(Access.ActorId, Access.Email, Access.SubjectProfileId, Token);
        var Job = await Store.GetAsync(Id, Token);
        if (Job is null || Job.ActorId != Access.ActorId || Job.Subject != Access.SubjectProfileId) throw new KeyNotFoundException();
        if (Cancel && !CodingJobs.Terminal(Job.Status))
        {
            if (!await Store.CancelAsync(Job.Id, Token) && (await Store.GetAsync(Job.Id, Token))?.Status == "Publishing")
                throw new InvalidOperationException("Publication is being reconciled; inspect the resulting PR before closing it.");
        }
        return (await Store.GetAsync(Id, Token))!.View;
    }
}

internal sealed class CodingJobTools(IServiceScopeFactory Scopes)
{
    public Task<string> StartAsync(AgentAccessContext Access, string Instruction, string RequestKey, CancellationToken Token,
        Guid? PreviousJobId = null, string? FollowUp = null, bool AllowNoChanges = false, bool Supervise = false) => InvokeAsync(async Service =>
    {
        var Job = await Service.StartAsync(Access, new(Instruction, RequestKey)
        {
            PreviousJobId = PreviousJobId, FollowUp = FollowUp, AllowNoChanges = AllowNoChanges, Supervise = Supervise
        }, Token);
        return new { job = Job, dashboard = $"/coding-jobs?profileId={Uri.EscapeDataString(Access.SubjectProfileId)}",
            message = "Inspect this job for its status, validation and pull request." };
    });
    public Task<string> InspectAsync(AgentAccessContext Access, Guid? Id, bool Cancel, CancellationToken Token) => InvokeAsync(async Service =>
        Id is { } Value ? (object)await Service.InspectAsync(Access, Value, Cancel, Token) : await Service.ListAsync(Access, Token));
    public Task<string> QuestionsAsync(AgentAccessContext Access, Guid? Id, string? Answer, CancellationToken Token) => InvokeAsync(async Service =>
        Id is { } Value
            ? await Service.AnswerQuestionAsync(Access, Value, Answer ?? "", true, Token)
            : await Service.QuestionsAsync(Access, CodingJobService.ConversationId(Access) ?? throw new UnauthorizedAccessException(), Token));
    private async Task<string> InvokeAsync(Func<CodingJobService, Task<object>> Action)
    {
        using var Scope = Scopes.CreateScope();
        try { return JsonSerializer.Serialize(await Action(Scope.ServiceProvider.GetRequiredService<CodingJobService>())); }
        catch (Exception E) when (E is ArgumentException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException or HttpRequestException)
        { return JsonSerializer.Serialize(new { error = E is HttpRequestException ? "Repository access or protection verification failed. Check coding settings." : E.Message }); }
    }
}

internal sealed class CodingJobCoordinator(CodingJobStore Store, IServiceScopeFactory Scopes, IBus Bus, ILogger<CodingJobCoordinator> Logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken Token) { await Store.InitializeAsync(Token); await base.StartAsync(Token); }
    protected override async Task ExecuteAsync(CancellationToken Token)
    {
        while (!Token.IsCancellationRequested)
        {
            try { await SweepAsync(Token); }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { break; }
            catch (Exception E) { Logger.LogError(new EventId(4401), E, "Coding reconciliation failed with {ExceptionType}", E.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(10), Token);
        }
    }

    internal async Task SweepAsync(CancellationToken Token)
    {
        // Session lock spans publication so replicas cannot publish simultaneously. Pool disposal releases it explicitly.
        await using var Connection = await Store.OpenAsync(Token);
        await using var Lock = new NpgsqlCommand("SELECT pg_try_advisory_lock(947120040)", Connection);
        if (!(bool)(await Lock.ExecuteScalarAsync(Token))!) return;
        try
        {
            try
            {
                using var DeliveryScope = Scopes.CreateScope();
                await DeliveryScope.ServiceProvider.GetRequiredService<CodingConversation>().DeliverAsync(Token);
            }
            catch (Exception E) when (!Token.IsCancellationRequested)
            {
                Logger.LogWarning(new EventId(4405), "Coding conversation delivery will retry after {ExceptionType}", E.GetType().Name);
            }
            foreach (var Job in await Store.PendingAsync(Token))
            {
                if (CodingJobs.Terminal(Job.Status)) continue;
                using var Scope = Scopes.CreateScope();
                var Service = Scope.ServiceProvider.GetRequiredService<CodingJobService>();
                var ExpectedStatus = Job.Status;
                try
                {
                    await Service.RequireAsync(Job.ActorId, Job.Email, Job.Subject, Token);
                    if (!(await Store.SettingsAsync(Token)).View.Settings.Enabled) throw new UnauthorizedAccessException();
                    var Now = DateTimeOffset.UtcNow;
                    if (Job.Status != "Publishing" && (Job.Deadline <= Now || Job.Status == "Running" && Job.AttemptDeadline <= Now))
                    {
                        await Store.SaveAsync(Job with { Status = "Failed", Error = "Coding deadline exceeded; inspect retained artifacts and cleanup." }, Job.Status, Token);
                        continue;
                    }
                    if (Job.Status == "Queued" && (Job.ResumePhase is null
                        || !(await Store.ReadClarificationsAsync(Job.Id, Token)).Any(Question => Question.Answer is null)))
                        await (await Bus.GetSendEndpoint(new Uri("queue:" + CodingJobs.Queue))).Send(new ExecuteCodingJob(Job.Id), Token);
                    if (Job.Status is "ReadyToPublish" or "Publishing")
                    {
                        var Publishing = Job with { Status = "Publishing", Error = null };
                        if (!await Store.SaveAsync(Publishing, Job.Status, Token)) continue;
                        ExpectedStatus = "Publishing";
                        var Url = await Scope.ServiceProvider.GetRequiredService<ICodingPublisher>().PublishAsync(Publishing, Token);
                        await Store.SaveAsync(Publishing with { Status = "PrOpened", PullRequestUrl = Url }, "Publishing", Token);
                    }
                }
                catch (UnauthorizedAccessException)
                { await Store.SaveAsync(Job with { Status = "Failed", Error = "Coding permission revoked or runtime disabled. Check the stable branch for any publication already in flight." }, ExpectedStatus, Token); }
                catch (CodingArtifactTooLarge E)
                { await Store.SaveAsync(Job with { Status = "Failed", Error = E.Message }, ExpectedStatus, Token); }
                catch (ArgumentException)
                { await Store.SaveAsync(Job with { Status = "Failed", Error = "Invalid coding artifact. Inspect diagnostics." }, ExpectedStatus, Token); }
                catch (CodingRevisionConflict E)
                { await Store.SaveAsync(Job with { Status = "Failed", Error = E.Message }, ExpectedStatus, Token); }
                catch (Exception E) when (E is HttpRequestException or InvalidOperationException)
                {
                    // Publication may already have succeeded: preserve intent and reconcile its stable marker next sweep.
                    await Store.SaveAsync(Job with { Status = ExpectedStatus, Error = "Waiting for publication reconciliation. Check GitHub App access, review policy and the stable job branch if this persists." }, ExpectedStatus, Token);
                    Logger.LogWarning(new EventId(4402), "Coding job {JobId} awaiting reconciliation after {ExceptionType}", Job.Id, E.GetType().Name);
                }
            }
        }
        finally
        {
            await using var Unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(947120040)", Connection);
            await Unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
