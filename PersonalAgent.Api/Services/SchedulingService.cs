using PersonalAgent.Api.Models;
using PersonalAgent.Api.Configuration;

namespace PersonalAgent.Api.Services;

internal class SchedulingService(ILogger<SchedulingService> logger, ScheduledJobStore? jobs = null, ScheduledJobAuthorization? authorization = null)
{
    public async Task<ScheduleResult> ScheduleNotificationAsync(ScheduleNotificationRequest request, AgentAccessContext access, CancellationToken cancellationToken = default)
        => (await ScheduleNotificationsAsync([request], access, cancellationToken))[0];

    public async Task<IReadOnlyList<ScheduleResult>> ScheduleNotificationsAsync(IReadOnlyList<ScheduleNotificationRequest> requests, AgentAccessContext access, CancellationToken cancellationToken)
    {
        if (requests.Count is < 1 or > 32) throw new ArgumentException("Schedule between 1 and 32 reminders per batch.");
        if (jobs is null || authorization is null) throw new InvalidOperationException("Scheduled job storage is unavailable.");
        var batch = requests.Select(request => PrepareNotification(request, access)).ToList();
        var current = await authorization.ForExecutionAsync(batch[0], cancellationToken);
        if (current is null || current.Role != access.Role)
            throw new UnauthorizedAccessException("Current scheduling permission is required.");
        await jobs.CreateBatchAsync(batch, cancellationToken);
        return batch.Select(job => new ScheduleResult(job.TaskId, job.ExecuteAt, job.CorrelationId,
            job.ExecuteAt <= job.CreatedAt ? "ScheduledImmediate" : "ScheduledDelayed")).ToList();
    }

    private static ScheduledJob PrepareNotification(ScheduleNotificationRequest request, AgentAccessContext access)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body)
            || request.Title.Length > PersonalAgentConstants.MaxMessageLength || request.Body.Length > PersonalAgentConstants.MaxMessageLength)
            throw new ArgumentException("The notification title or body is empty or too long.");
        var subject = ScheduledJobStore.Subject(request.TenantId, request.UserId);
        if (!string.Equals(subject, access.SubjectProfileId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Current scheduling access is required.");
        DateTimeOffset executeAtUtc;
        TimeSpan? recurrence;
        try
        {
            executeAtUtc = SchedulingTimeParser.ResolveExecuteAtUtc(request.Delay, request.ExecuteAt, request.When, request.TimeZoneId, DateTimeOffset.UtcNow);
            recurrence = SchedulingTimeParser.ParseRecurrence(request.RepeatEvery);
        }
        catch (InvalidOperationException Exception) { throw new ArgumentException("Invalid scheduling time.", Exception); }
        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob(Guid.NewGuid(), access.ActorId, access.Email, subject, request.Title.Trim(), now,
            executeAtUtc, "Scheduled", null, access.SessionId, null, false, request.CorrelationId ?? Guid.NewGuid(), 0, now)
        {
            JobType = "Notification",
            Notification = new(request.Title.Trim(), request.Body.Trim(), request.DeepLink),
            RecurrenceInterval = recurrence
        };
        return job;
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
        TimeSpan? recurrence;
        try
        {
            executeAtUtc = SchedulingTimeParser.ResolveExecuteAtUtc(request.Delay, request.ExecuteAt, request.When, request.TimeZoneId, DateTimeOffset.UtcNow);
            recurrence = SchedulingTimeParser.ParseRecurrence(request.RepeatEvery);
        }
        catch (InvalidOperationException Exception) { throw new ArgumentException("Invalid scheduling time.", Exception); }
        var correlationId = request.CorrelationId ?? Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob(taskId, access.ActorId, access.Email, subject, request.Instruction.Trim(), now,
            executeAtUtc, "Scheduled", null, access.SessionId, null, request.NotifyOnCompletion, correlationId, 0, now)
        {
            RecurrenceInterval = recurrence
        };
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
