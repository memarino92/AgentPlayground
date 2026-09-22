using System.Text.Json;

using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal sealed class ScheduledJobManagementService(
    ScheduledJobStore Store,
    ScheduledJobAuthorization Authorization,
    ScheduledJobExecutionService Execution)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> ListToolAsync(AgentAccessContext Access, string? Status, string? Before, CancellationToken Token)
    {
        if (!await IsCurrentAccessAsync(Access, Token)) return "Unable to list scheduled jobs: current access was denied.";
        DateTimeOffset? Cursor = null;
        if (!string.IsNullOrWhiteSpace(Before))
        {
            if (!DateTimeOffset.TryParse(Before, out var Parsed))
                return "Unable to list scheduled jobs: before must be an ISO-8601 datetime.";
            Cursor = Parsed;
        }
        var Jobs = await Store.ListAsync(Access.SubjectProfileId, Access.Role == AgentRoles.Coach ? Access.ActorId : null, Status, Cursor, Token);
        return JsonSerializer.Serialize(Jobs.Select(Summary), JsonOptions);
    }

    public async Task<string> GetToolAsync(AgentAccessContext Access, Guid JobId, CancellationToken Token)
    {
        var Job = await AccessibleAsync(Access, JobId, Token);
        if (Job is null) return "Scheduled job was not found or is no longer accessible.";
        return JsonSerializer.Serialize(new ScheduledJobDetail(Job, await Store.GetAttemptsAsync(JobId, Token)), JsonOptions);
    }

    public async Task<string> UpdateToolAsync(AgentAccessContext Access, Guid JobId, string? Instruction, string? Title, string? Body,
        string? Delay, string? ExecuteAt, string? When, string? TimeZoneId, bool? NotifyOnCompletion, CancellationToken Token)
    {
        var TimingCount = CountValues(Delay, ExecuteAt, When);
        if (TimingCount > 1) return "Unable to update scheduled job: provide at most one of delay, executeAt, or when.";
        if (TimingCount == 0 && Instruction is null && Title is null && Body is null && NotifyOnCompletion is null)
            return "Unable to update scheduled job: provide at least one change.";
        await using var Connection = await Store.OpenAsync(Token);
        if (!await Store.TryLockAsync(Connection, JobId, Token)) return "Unable to update scheduled job: it is currently active.";
        try
        {
            var Job = await Store.GetAsync(Connection, JobId, Token);
            if (Job is null || !await CanAccessAsync(Access, Job, Token)) return "Scheduled job was not found or is no longer accessible.";
            if (Job.Status is not ("Scheduled" or "Retrying")) return "Unable to update scheduled job: only pending jobs can be updated.";
            DateTimeOffset Due = Job.ExecuteAt;
            if (TimingCount == 1)
            {
                DateTimeOffset? Absolute = null;
                if (!string.IsNullOrWhiteSpace(ExecuteAt))
                {
                    if (!DateTimeOffset.TryParse(ExecuteAt, out var Parsed))
                        return "Unable to update scheduled job: executeAt must be an ISO-8601 datetime.";
                    Absolute = Parsed;
                }
                try { Due = SchedulingTimeParser.ResolveExecuteAtUtc(Delay, Absolute, When, TimeZoneId, DateTimeOffset.UtcNow); }
                catch (InvalidOperationException Error) { return $"Unable to update scheduled job: {Error.Message}"; }
            }
            if (Job.JobType == "AgentTask")
            {
                if (Title is not null || Body is not null) return "Unable to update scheduled job: title and body apply only to notifications.";
                if (Instruction is not null && (string.IsNullOrWhiteSpace(Instruction) || Instruction.Length > PersonalAgentConstants.MaxMessageLength))
                    return "Unable to update scheduled job: instruction is empty or too long.";
                Job = Job with { Instruction = Instruction?.Trim() ?? Job.Instruction, NotifyOnCompletion = NotifyOnCompletion ?? Job.NotifyOnCompletion };
            }
            else if (Job.JobType == "Notification")
            {
                if (Instruction is not null || NotifyOnCompletion is not null)
                    return "Unable to update scheduled job: instruction and notifyOnCompletion apply only to agent tasks.";
                var NewTitle = Title?.Trim() ?? Job.Notification?.Title;
                var NewBody = Body?.Trim() ?? Job.Notification?.Body;
                if (string.IsNullOrWhiteSpace(NewTitle) || string.IsNullOrWhiteSpace(NewBody)
                    || NewTitle.Length > PersonalAgentConstants.MaxMessageLength || NewBody.Length > PersonalAgentConstants.MaxMessageLength)
                    return "Unable to update scheduled job: notification title or body is empty or too long.";
                Job = Job with { Instruction = NewTitle, Notification = new(NewTitle, NewBody, Job.Notification?.DeepLink) };
            }
            else return $"Unable to update scheduled job: job type '{Job.JobType}' is not editable.";
            Job = Job with { ExecuteAt = Due, Status = "Scheduled", Outcome = null, UpdatedAt = DateTimeOffset.UtcNow };
            return await Store.UpdatePendingAsync(Connection, Job, Token)
                ? $"Updated scheduled job {Job.TaskId} for {Job.ExecuteAt:O}."
                : "Unable to update scheduled job: it is no longer pending.";
        }
        finally { await Store.UnlockAsync(Connection, JobId); }
    }

    public async Task<string> CancelToolAsync(AgentAccessContext Access, Guid JobId, CancellationToken Token)
    {
        if (await AccessibleAsync(Access, JobId, Token) is null) return "Scheduled job was not found or is no longer accessible.";
        return await Execution.CancelAsync(JobId, Token)
            ? $"Cancelled scheduled job {JobId}."
            : "Unable to cancel scheduled job: only pending jobs can be cancelled.";
    }

    private async Task<ScheduledJob?> AccessibleAsync(AgentAccessContext Access, Guid Id, CancellationToken Token)
    {
        var Job = await Store.GetAsync(Id, Token);
        return Job is not null && await CanAccessAsync(Access, Job, Token) ? Job : null;
    }

    private async Task<bool> CanAccessAsync(AgentAccessContext Access, ScheduledJob Job, CancellationToken Token) =>
        await IsCurrentAccessAsync(Access, Token)
        && string.Equals(Job.SubjectProfileId, Access.SubjectProfileId, StringComparison.OrdinalIgnoreCase)
        && (Access.Role == AgentRoles.Owner || string.Equals(Job.ActorId, Access.ActorId, StringComparison.Ordinal));

    private async Task<bool> IsCurrentAccessAsync(AgentAccessContext Access, CancellationToken Token)
    {
        var Current = await Authorization.ResolveAsync(Access.ActorId, Access.Email, Access.SubjectProfileId, Token);
        return Current is not null && Current.Role == Access.Role;
    }

    private static object Summary(ScheduledJob Job) => new
    {
        Job.TaskId, Job.JobType, Job.Instruction, Job.ExecuteAt, Job.Status, Job.Outcome,
        Job.NotifyOnCompletion, NotificationTitle = Job.Notification?.Title, NotificationBody = Job.Notification?.Body
    };

    private static int CountValues(params string?[] Values) => Values.Count(Value => !string.IsNullOrWhiteSpace(Value));
}
