using System.Text.Json;

using MassTransit;
using Npgsql;

using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Coding;

internal sealed class CodingJobService(CodingJobStore Store, ScheduledJobAuthorization Subjects, ToolAccessService Tools, ICodingPublisher Publisher)
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
        if (string.IsNullOrWhiteSpace(Request.Instruction) || Request.Instruction.Length > 8000 || !Guid.TryParse(Request.RequestKey, out _))
            throw new ArgumentException("An instruction of 1–8000 characters and a stable UUID requestKey are required.");
        var Settings = (await Store.SettingsAsync(Token)).View.Settings;
        if (!Settings.Enabled) throw new InvalidOperationException("Configure and enable platform coding in Settings first.");
        var BaseSha = await Publisher.PrepareAsync(Settings, Token);
        var Id = Guid.NewGuid(); var Now = DateTimeOffset.UtcNow;
        var Job = await Store.CreateAsync(new()
        {
            Id = Id, ActorId = Access.ActorId, Email = Access.Email, Subject = Access.SubjectProfileId,
            Instruction = Request.Instruction.Trim(), Settings = Settings, BaseSha = BaseSha,
            Branch = $"feat/platform-improvement-{Id:N}", CreatedAt = Now, Deadline = Now.AddMinutes(Settings.MaxMinutes + 5)
        }, Request.RequestKey, Token);
        if (Job.Instruction != Request.Instruction.Trim()) throw new InvalidOperationException("requestKey already identifies a different instruction.");
        return Job.View;
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
            if (Job.Status == "Publishing") throw new InvalidOperationException("Publication is being reconciled; inspect the resulting PR before closing it.");
            await Store.SaveAsync(Job with { Status = "Cancelled", Error = "Cancelled by owner." }, Job.Status, Token);
        }
        return (await Store.GetAsync(Id, Token))!.View;
    }
}

internal sealed class CodingJobTools(IServiceScopeFactory Scopes)
{
    public Task<string> StartAsync(AgentAccessContext Access, string Instruction, string RequestKey, CancellationToken Token) => InvokeAsync(async Service =>
    {
        var Job = await Service.StartAsync(Access, new(Instruction, RequestKey), Token);
        return new { job = Job, dashboard = $"/coding-jobs?profileId={Uri.EscapeDataString(Access.SubjectProfileId)}", message = "Queued. A PR has not been created yet. Inspect this job for results." };
    });
    public Task<string> InspectAsync(AgentAccessContext Access, Guid? Id, bool Cancel, CancellationToken Token) => InvokeAsync(async Service =>
        Id is { } Value ? (object)await Service.InspectAsync(Access, Value, Cancel, Token) : await Service.ListAsync(Access, Token));
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
            catch (Exception E) { Logger.LogError(new EventId(4401), "Coding reconciliation failed with {ExceptionType}", E.GetType().Name); }
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
                    if (Job.Deadline <= DateTimeOffset.UtcNow && Job.Status != "Publishing")
                    {
                        await Store.SaveAsync(Job with { Status = "Failed", Error = "Coding deadline exceeded; inspect retained artifacts and cleanup." }, Job.Status, Token);
                        continue;
                    }
                    if (Job.Status == "Queued")
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
                catch (ArgumentException)
                { await Store.SaveAsync(Job with { Status = "Failed", Error = "Invalid coding artifact. Inspect diagnostics." }, ExpectedStatus, Token); }
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
