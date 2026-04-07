using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class SchedulingService(IBus bus, ILogger<SchedulingService> logger)
{
    public async Task<ScheduleResult> ScheduleNotificationAsync(ScheduleNotificationRequest request, CancellationToken cancellationToken = default)
    {
        var executeAtUtc = SchedulingTimeParser.ResolveExecuteAtUtc(request.Delay, request.ExecuteAt, request.When, request.TimeZoneId, DateTimeOffset.UtcNow);
        var correlationId = request.CorrelationId ?? Guid.NewGuid();
        var notificationId = Guid.NewGuid();

        var payload = new NotificationRequested
        {
            NotificationId = notificationId,
            TenantId = request.TenantId.Trim(),
            UserId = request.UserId.Trim(),
            CorrelationId = correlationId,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            ExecuteAtUtc = executeAtUtc,
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            DeepLink = request.DeepLink,
            Source = "Api"
        };

        await bus.Publish(payload, cancellationToken);
        logger.LogInformation(
            "Published NotificationRequested {NotificationId} for tenant {TenantId}, user {UserId}, executeAtUtc {ExecuteAtUtc}",
            notificationId,
            payload.TenantId,
            payload.UserId,
            executeAtUtc);

        return new ScheduleResult(notificationId, executeAtUtc, correlationId, executeAtUtc <= DateTimeOffset.UtcNow ? "ScheduledImmediate" : "ScheduledDelayed");
    }

    public async Task<ScheduleResult> ScheduleAgentTaskAsync(ScheduleAgentTaskRequest request, CancellationToken cancellationToken = default)
    {
        var executeAtUtc = SchedulingTimeParser.ResolveExecuteAtUtc(request.Delay, request.ExecuteAt, request.When, request.TimeZoneId, DateTimeOffset.UtcNow);
        var correlationId = request.CorrelationId ?? Guid.NewGuid();
        var taskId = Guid.NewGuid();

        var payload = new AgentTaskScheduled
        {
            TaskId = taskId,
            TenantId = request.TenantId.Trim(),
            UserId = request.UserId.Trim(),
            CorrelationId = correlationId,
            RequestedAtUtc = DateTimeOffset.UtcNow,
            ExecuteAtUtc = executeAtUtc,
            Instruction = request.Instruction.Trim(),
            NotifyOnCompletion = request.NotifyOnCompletion
        };

        await bus.Publish(payload, cancellationToken);
        logger.LogInformation(
            "Published AgentTaskScheduled {TaskId} for tenant {TenantId}, user {UserId}, executeAtUtc {ExecuteAtUtc}",
            taskId,
            payload.TenantId,
            payload.UserId,
            executeAtUtc);

        return new ScheduleResult(taskId, executeAtUtc, correlationId, executeAtUtc <= DateTimeOffset.UtcNow ? "ScheduledImmediate" : "ScheduledDelayed");
    }
}
