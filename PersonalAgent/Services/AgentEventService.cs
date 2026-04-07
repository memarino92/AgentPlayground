using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal class AgentEventService
{
    private readonly IBus _bus;
    private readonly ChatClientAgent _eventAgent;
    private readonly ILogger<AgentEventService> _logger;

    public AgentEventService(
        IOptions<ApiKeyOptions> apiKeyOptions,
        IBus bus,
        ChatModelCatalog chatModelCatalog,
        ILogger<AgentEventService> logger,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        _bus = bus;
        _logger = logger;

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
}
