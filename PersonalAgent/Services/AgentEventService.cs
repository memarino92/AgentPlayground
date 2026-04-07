using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal class AgentEventService
{
    private readonly IBus _bus;
    private readonly ChatClientAgent _eventAgent;
    private readonly ILogger<AgentEventService> _logger;
    private readonly SchedulingService _schedulingService;

    public AgentEventService(
        IOptions<ApiKeyOptions> apiKeyOptions,
        IBus bus,
        ChatModelCatalog chatModelCatalog,
        SchedulingService schedulingService,
        ILogger<AgentEventService> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        _bus = bus;
        _logger = logger;
        _schedulingService = schedulingService;

        var eventChatClient = new OpenAIClient(apiKey)
            .GetChatClient(chatModelCatalog.GetDefaultModel().Id);

        _eventAgent = eventChatClient
            .AsIChatClient()
            .AsBuilder()
            .BuildAIAgent(
                instructions: "You generate concise follow-up messages for bus events.",
                name: "PersonalAgentEventGenerator",
                description: "Generates deterministic follow-up text for bus events.",
                loggerFactory: loggerFactory,
                services: serviceProvider);
    }

    public async Task GenerateAndPublishTestMessageAsync(TestEventRequested request, CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = request.CorrelationId,
            ["RequestedBy"] = request.RequestedBy,
            ["Source"] = request.Source
        });

        var session = await _eventAgent.CreateSessionAsync(cancellationToken);
        var prompt = $"""
            A bus event was received.
            CorrelationId: {request.CorrelationId}
            RequestedBy: {request.RequestedBy}
            Source: {request.Source}
            Original message: {request.Message}

            Generate a short sentence confirming that the agent consumed the event.
            Do not mention tools, function calls, or internal implementation details.
            """;

        _logger.LogInformation("Generating follow-up message for consumed bus event");
        var response = await _eventAgent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await PublishGeneratedMessageAsync(
            request.CorrelationId,
            promptSummary: "Bus test event received",
            message: response.ToString(),
            cancellationToken);
    }

    public async Task<string> PublishGeneratedMessageAsync(Guid correlationId, string promptSummary, string message, CancellationToken cancellationToken = default)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["PromptSummary"] = promptSummary
        });

        var payload = new AgentGeneratedTestMessage
        {
            CorrelationId = correlationId,
            GeneratedAt = DateTimeOffset.UtcNow,
            GeneratedBy = "PersonalAgent",
            PromptSummary = string.IsNullOrWhiteSpace(promptSummary) ? "Bus test event received" : promptSummary,
            Message = message
        };

        await _bus.Publish(payload, cancellationToken);
        _logger.LogInformation("Published generated bus follow-up message");
        return $"Published generated test message for {correlationId}";
    }

    public async Task<string> PublishGeneratedMessageToolAsync(string correlationId, string promptSummary, string message, CancellationToken cancellationToken = default)
    {
        var parsedCorrelationId = Guid.TryParse(correlationId, out var value)
            ? value
            : Guid.NewGuid();

        if (parsedCorrelationId == Guid.Empty)
            parsedCorrelationId = Guid.NewGuid();

        if (!string.Equals(correlationId, parsedCorrelationId.ToString(), StringComparison.OrdinalIgnoreCase))
            _logger.LogWarning("Tool supplied invalid correlation id '{CorrelationId}', generated fallback id {FallbackCorrelationId}", correlationId, parsedCorrelationId);

        return await PublishGeneratedMessageAsync(parsedCorrelationId, promptSummary, message, cancellationToken);
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

    public async Task<string> ScheduleNotificationToolAsync(string profileId, string title, string body, string? delay, string? executeAt, string? when, string? timeZoneId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return "Unable to schedule notification: profileId is required.";
        if (string.IsNullOrWhiteSpace(title)) return "Unable to schedule notification: title is required.";
        if (string.IsNullOrWhiteSpace(body)) return "Unable to schedule notification: body is required.";

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
        }, cancellationToken);

        return $"Scheduled notification {result.Id} at {result.ExecuteAtUtc:O}";
    }

    public async Task<string> ScheduleAgentTaskToolAsync(string profileId, string instruction, string? delay, string? executeAt, string? when, string? timeZoneId, bool notifyOnCompletion = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return "Unable to schedule agent task: profileId is required.";
        if (string.IsNullOrWhiteSpace(instruction)) return "Unable to schedule agent task: instruction is required.";

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
        }, cancellationToken);

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
}
