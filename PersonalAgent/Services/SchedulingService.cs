using PersonalAgent.Models;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal class SchedulingService(ILogger<SchedulingService> logger, ScheduledJobStore? jobs = null, ScheduledJobAuthorization? authorization = null)
{
    public async Task<ScheduleResult> ScheduleNotificationAsync(ScheduleNotificationRequest request, AgentAccessContext access, CancellationToken cancellationToken = default)
    {
        if (jobs is null || authorization is null) throw new InvalidOperationException("Scheduled job storage is unavailable.");
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body)
            || request.Title.Length > PersonalAgentConstants.MaxMessageLength || request.Body.Length > PersonalAgentConstants.MaxMessageLength)
            throw new ArgumentException("The notification title or body is empty or too long.");
        var subject = ScheduledJobStore.Subject(request.TenantId, request.UserId);
        var current = await authorization.ResolveAsync(access.ActorId, access.Email, subject, cancellationToken);
        if (current is null || current.Role != access.Role || !string.Equals(subject, access.SubjectProfileId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Current scheduling access is required.");
        DateTimeOffset executeAtUtc;
        try { executeAtUtc = SchedulingTimeParser.ResolveExecuteAtUtc(request.Delay, request.ExecuteAt, request.When, request.TimeZoneId, DateTimeOffset.UtcNow); }
        catch (InvalidOperationException Exception) { throw new ArgumentException("Invalid scheduling time.", Exception); }
        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob(Guid.NewGuid(), access.ActorId, access.Email, subject, request.Title.Trim(), now,
            executeAtUtc, "Scheduled", null, access.SessionId, null, false, request.CorrelationId ?? Guid.NewGuid(), 0, now)
        {
            JobType = "Notification",
            Notification = new(request.Title.Trim(), request.Body.Trim(), request.DeepLink)
        };
        if (await authorization.ForExecutionAsync(job, cancellationToken) is null)
            throw new UnauthorizedAccessException("Current scheduling permission is required.");
        await jobs.CreateAsync(job, cancellationToken);
        logger.LogInformation("Persisted notification job {JobId} for subject {Subject} due {ExecuteAt}", job.TaskId, subject, executeAtUtc);
        return new ScheduleResult(job.TaskId, executeAtUtc, job.CorrelationId, executeAtUtc <= now ? "ScheduledImmediate" : "ScheduledDelayed");
    }

    public async Task<ScheduleResult> ScheduleAgentTaskAsync(ScheduleAgentTaskRequest request, AgentAccessContext access, CancellationToken cancellationToken = default)
    {
        if (jobs is null || authorization is null) throw new InvalidOperationException("Scheduled job storage is unavailable.");
        if (string.IsNullOrWhiteSpace(request.Instruction) || request.Instruction.Length > PersonalAgentConstants.MaxMessageLength)
            throw new ArgumentException("The scheduled instruction is empty or too long.");
        var subject = ScheduledJobStore.Subject(request.TenantId, request.UserId);
        var current = await authorization.ResolveAsync(access.ActorId, access.Email, subject, cancellationToken);
        if (current is null || current.Role != access.Role || !string.Equals(subject, access.SubjectProfileId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Current scheduling access is required.");
        DateTimeOffset executeAtUtc;
        try { executeAtUtc = SchedulingTimeParser.ResolveExecuteAtUtc(request.Delay, request.ExecuteAt, request.When, request.TimeZoneId, DateTimeOffset.UtcNow); }
        catch (InvalidOperationException Exception) { throw new ArgumentException("Invalid scheduling time.", Exception); }
        var correlationId = request.CorrelationId ?? Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob(taskId, access.ActorId, access.Email, subject, request.Instruction.Trim(), now,
            executeAtUtc, "Scheduled", null, access.SessionId, null, request.NotifyOnCompletion, correlationId, 0, now);
        if (await authorization.ForExecutionAsync(job, cancellationToken) is null)
            throw new UnauthorizedAccessException("Current scheduling permission is required.");
        await jobs.CreateAsync(job, cancellationToken);
        logger.LogInformation(
            "Published AgentTaskScheduled {TaskId} for tenant {TenantId}, user {UserId}, executeAtUtc {ExecuteAtUtc}",
            taskId,
            request.TenantId,
            request.UserId,
            executeAtUtc);

        return new ScheduleResult(taskId, executeAtUtc, correlationId, executeAtUtc <= DateTimeOffset.UtcNow ? "ScheduledImmediate" : "ScheduledDelayed");
    }
}
