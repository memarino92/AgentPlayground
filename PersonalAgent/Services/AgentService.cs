using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using System.Collections.Concurrent;

namespace PersonalAgent.Services;

internal class AgentService
{
    private readonly AIAgent _agent;
    private readonly IBus _bus;
    private readonly ILogger<AgentService> _logger;
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, List<ConversationMessage>> _messageHistory = new();

    public AgentService(IOptions<ApiKeyOptions> apiKeyOptions, IBus bus, ILogger<AgentService> logger, IServiceProvider serviceProvider)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key not found. In production set OPENAI_API_KEY env var; for local dev use: dotnet user-secrets set OpenApiKey \"your-key\" --project PersonalAgent");

        _bus = bus;
        _logger = logger;

        var publishTool = AIFunctionFactory.Create(PublishGeneratedMessageAsync, "publish_generated_test_message",
            "Publish a generated test message to the shared MassTransit bus.");

        _agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4o-mini")
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .BuildAIAgent(
                instructions: "You are a helpful personal assistant. When asked to respond to a bus test event, you must call the publish_generated_test_message tool exactly once with a concise generated message describing that you received the event.",
                name: "PersonalAgent",
                description: "Personal agent that can publish follow-up messages to the shared event bus.",
                tools: [publishTool],
                loggerFactory: logger is ILoggerFactory loggerFactory ? loggerFactory : null,
                services: serviceProvider);
    }

    public async Task<string> CreateSessionAsync()
    {
        var sessionId = Guid.NewGuid().ToString();
        var session = await _agent.CreateSessionAsync();
        _sessions[sessionId] = session;
        _messageHistory[sessionId] = [];
        return sessionId;
    }

    public async Task<string?> SendMessageAsync(string sessionId, string message)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        _messageHistory[sessionId].Add(new ConversationMessage("user", message));
        var response = await _agent.RunAsync(message, session);
        var responseText = response.ToString();
        _messageHistory[sessionId].Add(new ConversationMessage("assistant", responseText));

        return responseText;
    }

    public List<ConversationMessage>? GetSessionMessages(string sessionId) =>
        _messageHistory.TryGetValue(sessionId, out var messages) ? messages : null;

    public async Task GenerateAndPublishTestMessageAsync(TestEventRequested request, CancellationToken cancellationToken)
    {
        var session = await _agent.CreateSessionAsync(cancellationToken);
        var prompt = $"""
            A bus event was received.
            CorrelationId: {request.CorrelationId}
            RequestedBy: {request.RequestedBy}
            Source: {request.Source}
            Original message: {request.Message}

            Use the publish_generated_test_message tool exactly once.
            The generated message should be a short sentence confirming the agent consumed the event.
            """;

        _logger.LogInformation("Agent generating follow-up message for correlation {CorrelationId}", request.CorrelationId);
        await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
    }

    public async Task<string> PublishGeneratedMessageAsync(Guid correlationId, string promptSummary, string message, CancellationToken cancellationToken = default)
    {
        var payload = new AgentGeneratedTestMessage
        {
            CorrelationId = correlationId,
            GeneratedAt = DateTimeOffset.UtcNow,
            GeneratedBy = "PersonalAgent",
            PromptSummary = string.IsNullOrWhiteSpace(promptSummary) ? "Bus test event received" : promptSummary,
            Message = message
        };

        await _bus.Publish(payload, cancellationToken);
        _logger.LogInformation("Agent published generated test message for correlation {CorrelationId}", correlationId);
        return $"Published generated test message for {correlationId}";
    }
}
