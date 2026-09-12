using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class AgentEventService
{
    private readonly IBus _bus;
    private readonly ILogger<AgentEventService> _logger;
    private readonly SchedulingService _schedulingService;

    public AgentEventService(IBus bus, SchedulingService schedulingService, ILogger<AgentEventService> logger)
    {
        _bus = bus;
        _schedulingService = schedulingService;
        _logger = logger;
    }

    public async Task<string> PublishMobileNotificationToolAsync(string profileId, string title, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return "Unable to publish notification: profileId is required.";
        if (string.IsNullOrWhiteSpace(title))
            return "Unable to publish notification: title is required.";
        if (string.IsNullOrWhiteSpace(body))
            return "Unable to publish notification: body is required.";

        var payload = new DevicePushNotificationRequested
        {
            NotificationId = Guid.NewGuid(),
            RequestedAt = DateTimeOffset.UtcNow,
            ProfileId = profileId.Trim(),
            NotificationType = "agent-notification",
            Title = title.Trim(),
            Body = body.Trim(),
            Data = new Dictionary<string, string>
            {
                ["source"] = "agent-tool"
            }
        };

        await _bus.Publish(payload, cancellationToken);

        _logger.LogInformation(
            "Published mobile notification event for profile {ProfileId} with title {Title}",
            payload.ProfileId,
            payload.Title);

        return $"Published mobile notification for profile {payload.ProfileId}";
    }

    public async Task<string> ScheduleNotificationToolAsync(AgentAccessContext access, string title, string body, string? delay = null, string? executeAt = null, string? when = null, string? timeZoneId = null, CancellationToken cancellationToken = default)
    {
        var profileId = access.SubjectProfileId;
        if (string.IsNullOrWhiteSpace(profileId)) return "Unable to schedule notification: profileId is required.";
        if (string.IsNullOrWhiteSpace(title)) return "Unable to schedule notification: title is required.";
        if (string.IsNullOrWhiteSpace(body)) return "Unable to schedule notification: body is required.";
        if (!HasExactlyOneTimingInput(delay, executeAt, when)) return "Unable to schedule notification: provide exactly one of delay (e.g. PT5M), executeAt (ISO-8601 datetime), or when (natural time like tonight).";

        var (tenantId, userId) = ParseTenantAndUser(profileId);
        var parsedExecuteAt = DateTimeOffset.TryParse(executeAt, out var value) ? value : (DateTimeOffset?)null;
        var result = await _schedulingService.ScheduleNotificationAsync(new ScheduleNotificationRequest
        {
            TenantId = tenantId,
            UserId = userId,
            Title = title,
            Body = body,
            Delay = delay,
            ExecuteAt = parsedExecuteAt,
            When = when,
            TimeZoneId = timeZoneId
        }, access, cancellationToken);

        return $"Scheduled notification {result.Id} at {result.ExecuteAtUtc:O}";
    }

    public async Task<string> ScheduleAgentTaskToolAsync(AgentAccessContext access, string instruction, string? delay = null, string? executeAt = null, string? when = null, string? timeZoneId = null, bool notifyOnCompletion = true, CancellationToken cancellationToken = default)
    {
        var profileId = access.SubjectProfileId;
        if (string.IsNullOrWhiteSpace(profileId)) return "Unable to schedule agent task: profileId is required.";
        if (string.IsNullOrWhiteSpace(instruction)) return "Unable to schedule agent task: instruction is required.";
        if (!HasExactlyOneTimingInput(delay, executeAt, when)) return "Unable to schedule agent task: provide exactly one of delay (e.g. PT5M), executeAt (ISO-8601 datetime), or when (natural time like 'tonight').";

        var (tenantId, userId) = ParseTenantAndUser(profileId);
        var parsedExecuteAt = DateTimeOffset.TryParse(executeAt, out var value) ? value : (DateTimeOffset?)null;
        var result = await _schedulingService.ScheduleAgentTaskAsync(new ScheduleAgentTaskRequest
        {
            TenantId = tenantId,
            UserId = userId,
            Instruction = instruction,
            Delay = delay,
            ExecuteAt = parsedExecuteAt,
            When = when,
            TimeZoneId = timeZoneId,
            NotifyOnCompletion = notifyOnCompletion
        }, access, cancellationToken);

        return $"Scheduled agent task {result.Id} at {result.ExecuteAtUtc:O}";
    }

    private static (string TenantId, string UserId) ParseTenantAndUser(string profileId)
    {
        var trimmed = profileId.Trim();
        var segments = trimmed.Split(':', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return segments.Length is 2
            ? (segments[0], segments[1])
            : (string.Empty, trimmed);
    }

    public Task<string> GetCurrentDateTimeToolAsync(string? timeZoneId = null)
    {
        var utcNow = DateTimeOffset.UtcNow;
        var effectiveTimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? "America/New_York" : timeZoneId;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(effectiveTimeZoneId);
            var localNow = TimeZoneInfo.ConvertTime(utcNow, tz);
            return Task.FromResult(
                $"Current date and time in {tz.DisplayName}: {localNow:dddd, MMMM d, yyyy HH:mm:ss} (ISO-8601: {localNow:O})\n" +
                $"Current UTC date and time: {utcNow:dddd, MMMM d, yyyy HH:mm:ss} UTC (ISO-8601: {utcNow:O})");
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning("Unable to resolve timezone {TimeZoneId} for get_current_date_time, returning UTC", effectiveTimeZoneId);
            return Task.FromResult(
                $"Current UTC date and time: {utcNow:dddd, MMMM d, yyyy HH:mm:ss} UTC (ISO-8601: {utcNow:O}) " +
                $"(timezone '{effectiveTimeZoneId}' was not recognized)");
        }
    }

    private static bool HasExactlyOneTimingInput(string? delay, string? executeAt, string? when)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(delay)) count++;
        if (!string.IsNullOrWhiteSpace(executeAt)) count++;
        if (!string.IsNullOrWhiteSpace(when)) count++;
        return count is 1;
    }
}
