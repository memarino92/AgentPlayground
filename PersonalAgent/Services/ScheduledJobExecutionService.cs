using System.Text.Json;
using AgentPlayground.Contracts.Messaging.Commands;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal interface IScheduledJobRunner
{
    Task PrepareAsync(Guid SessionId, AgentAccessContext Access, CancellationToken Token);
    Task<string?> RunAsync(Guid SessionId, AgentAccessContext Access, string Instruction, CancellationToken Token);
    Task<string?> RecoverAsync(Guid SessionId, CancellationToken Token);
}

internal sealed class ScheduledJobRunner(IAgentSessionStore Sessions, IChatModelCatalog Models, AgentChatService Chat) : IScheduledJobRunner
{
    public async Task PrepareAsync(Guid SessionId, AgentAccessContext Access, CancellationToken Token)
    {
        if (await Sessions.GetSessionAsync(SessionId, Token) is { } Existing)
        {
            if (Existing.EffectiveActorId != Access.ActorId || Existing.ProfileId != Access.SubjectProfileId)
                throw new UnauthorizedAccessException("Job conversation ownership mismatch.");
            return;
        }
        var Model = (await Models.GetModelsAsync(Token)).FirstOrDefault(M => M.IsDefault)
            ?? throw new HttpRequestException("No chat model is available.");
        await Sessions.CreateSessionAsync(SessionId, Access, JsonSerializer.Serialize(new AgentSessionState(Model.Id, SessionId)), Token);
    }

    public Task<string?> RunAsync(Guid SessionId, AgentAccessContext Access, string Instruction, CancellationToken Token) =>
        Chat.SendMessageAsync(SessionId.ToString(), Access, Instruction, Token);

    public async Task<string?> RecoverAsync(Guid SessionId, CancellationToken Token) =>
        (await Sessions.GetSessionMessagesAsync(SessionId, Token))?.LastOrDefault(M => M.Role == "assistant")?.Content;
}

internal sealed class ScheduledJobExecutionService(
    ScheduledJobStore Store, ScheduledJobAuthorization Authorization, IScheduledJobRunner Runner,
    ILogger<ScheduledJobExecutionService> Logger)
{
    public async Task<ScheduledJobExecutionResponse> ExecuteAsync(ExecuteAgentTask Delivery, CancellationToken Token)
    {
        await using var Connection = await Store.OpenAsync(Token);
        if (!await Store.TryLockAsync(Connection, Delivery.TaskId, Token)) return new("Running", "Execution is already active.");
        try
        {
            var Job = await Store.GetAsync(Connection, Delivery.TaskId, Token);
            if (Job is null)
            {
                var Now = DateTimeOffset.UtcNow;
                Job = new(Delivery.TaskId, null, null, ScheduledJobStore.Subject(Delivery.TenantId, Delivery.UserId),
                    "Legacy scheduled task", Now, Delivery.ExecuteAtUtc, "Blocked",
                    "This legacy schedule has no trusted scheduler identity. Create a new schedule to run it.",
                    null, null, false, Delivery.CorrelationId, 0, Now);
                await Store.CreateAsync(Job, Token);
                return new(Job.Status, Job.Outcome);
            }
            if (IsTerminal(Job.Status)) return new(Job.Status, Job.Outcome);
            if (Job.ExecuteAt > DateTimeOffset.UtcNow) return new("Scheduled", "Not due yet.");
            var WasRunning = Job.Status == "Running";
            try
            {
                if (!WasRunning)
                {
                    if (Job.AttemptCount >= 5) return await FinishAsync("Failed", "Execution could not start after five attempts.");
                    if (Job.AttemptCount > 0) await Store.SaveAsync(Connection, Job, false, true, false, Token);
                    Job = Job with { Status = "Retrying", AttemptCount = Job.AttemptCount + 1, UpdatedAt = DateTimeOffset.UtcNow };
                    await Store.SaveAsync(Connection, Job, true, false, false, Token);
                }
                var Access = await Authorization.ForExecutionAsync(Job, Token);
                if (Access is null) return await FinishAsync("Blocked", "The scheduler no longer has permission to run this job.");
                if (WasRunning)
                {
                    var Recovered = Job.SessionId is not null ? await Runner.RecoverAsync(Guid.Parse(Job.SessionId), Token) : null;
                    return Recovered is not null
                        ? await FinishAsync("Completed", Recovered)
                        : await FinishAsync("NeedsReview", "Execution was interrupted. Tool effects may have occurred; this job will not be repeated automatically.");
                }
                // A deterministic conversation identity survives a failure between session creation and job update.
                var SessionId = Job.TaskId;
                await Runner.PrepareAsync(SessionId, Access, Token);
                Access = await Authorization.ForExecutionAsync(Job, Token);
                if (Access is null) return await FinishAsync("Blocked", "Scheduling access was revoked before execution.");
                Job = Job with { Status = "Running", SessionId = SessionId.ToString(), UpdatedAt = DateTimeOffset.UtcNow };
                await Store.SaveAsync(Connection, Job, false, false, false, Token);
                WasRunning = true;
                var Run = new ScheduledRunContext();
                var Result = await Runner.RunAsync(SessionId, Access with { SessionId = Job.SessionId, ScheduledRun = Run }, Job.Instruction, Token);
                if (Run.AuthorizationDenied) return await FinishAsync("Blocked", "Access was revoked during execution. Earlier authorized actions may have completed.");
                return Result is not null
                    ? await FinishAsync("Completed", Result)
                    : await FinishAsync("NeedsReview", "No durable response was saved. Review the job before taking further action.");
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { return await FinishAsync("Blocked", "Execution authorization was denied."); }
            catch (Exception Exception)
            {
                Logger.LogWarning(Exception, "Scheduled job {TaskId} interrupted; running={Running}", Job.TaskId, WasRunning);
                if (WasRunning)
                {
                    var Recovered = Job.SessionId is not null ? await Runner.RecoverAsync(Guid.Parse(Job.SessionId), Token) : null;
                    return Recovered is not null ? await FinishAsync("Completed", Recovered)
                        : await FinishAsync("NeedsReview", "Execution was interrupted. Tool effects may have occurred; automatic replay is disabled.");
                }
                if (Job.AttemptCount >= 5) return await FinishAsync("Failed", "Execution could not start after five attempts.");
                Job = Job with { Status = "Retrying", Outcome = "Execution could not start. A retry is scheduled.", UpdatedAt = DateTimeOffset.UtcNow };
                await Store.SaveAsync(Connection, Job, false, Job.AttemptCount > 0, false, Token);
                return new(Job.Status, Job.Outcome);
            }

            async Task<ScheduledJobExecutionResponse> FinishAsync(string Status, string Outcome)
            {
                Job = Job with { Status = Status, Outcome = Outcome, UpdatedAt = DateTimeOffset.UtcNow };
                await Store.SaveAsync(Connection, Job, false, Job.AttemptCount > 0, true, Token);
                return new(Job.Status, Job.Outcome);
            }
        }
        finally { await Store.UnlockAsync(Connection, Delivery.TaskId); }
    }

    public async Task<bool> CancelAsync(Guid Id, CancellationToken Token)
    {
        await using var Connection = await Store.OpenAsync(Token);
        if (!await Store.TryLockAsync(Connection, Id, Token)) return false;
        try
        {
            var Job = await Store.GetAsync(Connection, Id, Token);
            if (Job is null || Job.Status is not ("Scheduled" or "Retrying")) return false;
            await Store.SaveAsync(Connection, Job with { Status = "Cancelled", Outcome = "Cancelled before execution.", UpdatedAt = DateTimeOffset.UtcNow }, false, false, false, Token);
            return true;
        }
        finally { await Store.UnlockAsync(Connection, Id); }
    }

    private static bool IsTerminal(string Status) => Status is "Completed" or "Blocked" or "Cancelled" or "Failed" or "NeedsReview";
}
