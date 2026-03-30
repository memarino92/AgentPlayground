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
using System.Text.Json;

namespace PersonalAgent.Services;

internal class AgentService
{
    private readonly OpenAIClient _openAiClient;
    private readonly ChatClientAgent _eventAgent;
    private readonly IBus _bus;
    private readonly ChatModelCatalog _chatModelCatalog;
    private readonly ILogger<AgentService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();
    private readonly ConcurrentDictionary<string, ChatClientAgent> _sessionAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAgentSessionStore _sessionStore;
    private readonly SemanticMemoryService _semanticMemoryService;

    public AgentService(IOptions<ApiKeyOptions> apiKeyOptions, IBus bus, ChatModelCatalog chatModelCatalog, ILogger<AgentService> logger, IServiceProvider serviceProvider, IAgentSessionStore sessionStore, SemanticMemoryService semanticMemoryService)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key not found. In production set OPENAI_API_KEY env var; for local dev use: dotnet user-secrets set OpenApiKey \"your-key\" --project PersonalAgent");

        _openAiClient = new OpenAIClient(apiKey);
        _bus = bus;
        _chatModelCatalog = chatModelCatalog;
        _logger = logger;
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _semanticMemoryService = semanticMemoryService;

        var publishTool = AIFunctionFactory.Create(PublishGeneratedMessageAsync, "publish_generated_test_message",
            "Publish a generated test message to the shared MassTransit bus.");

        var eventChatClient = _openAiClient
            .GetChatClient(_chatModelCatalog.GetDefaultModel().Id);

        _eventAgent = eventChatClient
            .AsIChatClient()
            .AsBuilder()
            .BuildAIAgent(
                instructions: "You generate concise follow-up messages for bus events.",
                name: "PersonalAgentEventGenerator",
                description: "Generates deterministic follow-up text for bus events.",
                loggerFactory: logger is ILoggerFactory eventLoggerFactory ? eventLoggerFactory : null,
                services: serviceProvider);

        _sessionAgents.TryAdd(_chatModelCatalog.GetDefaultModel().Id, CreateSessionAgent(_chatModelCatalog.GetDefaultModel().Id, publishTool));
    }

    public async Task<(string SessionId, string ModelId)> CreateSessionAsync(string profileId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException("Profile id is required to create a chat session.");

        var selectedModel = _chatModelCatalog.FindModel(modelId)
            ?? throw new InvalidOperationException($"Chat model '{modelId}' is not available.");
        var sessionId = Guid.NewGuid();
        var sessionState = JsonSerializer.Serialize(new AgentSessionState(selectedModel.Id));
        await _sessionStore.CreateSessionAsync(sessionId, profileId, sessionState);
        return (sessionId.ToString(), selectedModel.Id);
    }

    public async Task<SessionSummaryPage> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new InvalidOperationException("Profile id is required to load chat sessions.");

        var normalizedPageSize = Math.Clamp(pageSize, 1, 50);
        var sessions = await _sessionStore.GetSessionsAsync(profileId, beforeActivityAt, beforeSessionId, normalizedPageSize + 1);
        var pageItems = sessions.Take(normalizedPageSize).ToList();
        var hasMore = sessions.Count > normalizedPageSize;
        var nextCursor = hasMore ? pageItems[^1] : null;

        return new SessionSummaryPage(
            pageItems.Select(session => new SessionSummary(session.SessionId, session.Snippet, session.LastActivityAt, session.CreatedAt)).ToList(),
            hasMore ? nextCursor?.LastActivityAt : null,
            hasMore ? nextCursor?.SessionId : null,
            hasMore);
    }

    public async Task<string?> SendMessageAsync(string sessionId, string profileId, string message)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId))
            return null;

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync();

        try
        {
            var persistedSession = await _sessionStore.GetSessionAsync(parsedSessionId);
            if (persistedSession is null || !string.Equals(persistedSession.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)) return null;

            var sessionState = DeserializeSessionState(persistedSession.SessionStateJson);
            var agent = GetSessionAgent(sessionState.ModelId);
            var session = await agent.CreateSessionAsync();
            var transcript = await _sessionStore.GetSessionMessagesAsync(parsedSessionId) ?? [];
            var recalledMemories = await _semanticMemoryService.RecallMemoriesAsync(persistedSession.ProfileId, message);
            var messages = transcript
                .Select(ToChatMessage)
                .ToList();

            if (recalledMemories.Count > 0)
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, BuildMemoryPrompt(recalledMemories)));

            messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, message));

            var response = await agent.RunAsync(messages, session, options: null, cancellationToken: default);
            var responseText = response.ToString();
            var wasSaved = await _sessionStore.SaveInteractionAsync(parsedSessionId, message, responseText, persistedSession.SessionStateJson);

            if (wasSaved)
                await _semanticMemoryService.StoreConversationMemoriesAsync(parsedSessionId, persistedSession.ProfileId, message, responseText);

            return wasSaved ? responseText : null;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<SessionConversation?> GetSessionMessagesAsync(string sessionId, string profileId)
    {
        if (!Guid.TryParse(sessionId, out var parsedSessionId)) return null;

        var persistedSession = await _sessionStore.GetSessionAsync(parsedSessionId);
        if (persistedSession is null || !string.Equals(persistedSession.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)) return null;

        var messages = await _sessionStore.GetSessionMessagesAsync(parsedSessionId);
        if (messages is null) return null;

        var sessionState = DeserializeSessionState(persistedSession.SessionStateJson);
        return new SessionConversation(sessionId, sessionState.ModelId, messages);
    }

    public async Task GenerateAndPublishTestMessageAsync(TestEventRequested request, CancellationToken cancellationToken)
    {
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

        _logger.LogInformation("Agent generating follow-up message for correlation {CorrelationId}", request.CorrelationId);
        var response = await _eventAgent.RunAsync(prompt, session, cancellationToken: cancellationToken);
        await PublishGeneratedMessageAsync(
            request.CorrelationId,
            promptSummary: "Bus test event received",
            message: response.ToString(),
            cancellationToken);
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

    private static Microsoft.Extensions.AI.ChatMessage ToChatMessage(ConversationMessage message) => new(message.Role switch
    {
        "assistant" => ChatRole.Assistant,
        _ => ChatRole.User
    }, message.Content);

    private ChatClientAgent GetSessionAgent(string modelId) => _sessionAgents.GetOrAdd(modelId, CreateSessionAgent);

    private ChatClientAgent CreateSessionAgent(string modelId)
    {
        var publishTool = AIFunctionFactory.Create(PublishGeneratedMessageAsync, "publish_generated_test_message",
            "Publish a generated test message to the shared MassTransit bus.");
        return CreateSessionAgent(modelId, publishTool);
    }

    private ChatClientAgent CreateSessionAgent(string modelId, AIFunction publishTool) => _openAiClient
        .GetChatClient(modelId)
        .AsIChatClient()
        .AsBuilder()
        .UseFunctionInvocation()
        .BuildAIAgent(
            instructions: """
                You are a helpful personal assistant.
                When asked to respond to a bus test event, you must call the publish_generated_test_message tool exactly once with a concise generated message describing that you received the event.
                This does not mean that you should respond to every user message with a bus event follow-up, only when you are specifically asked to generate a follow-up message for a bus event.
                """,
            name: "PersonalAgent",
            description: "Personal agent that can publish follow-up messages to the shared event bus.",
            tools: [publishTool],
            loggerFactory: _logger is ILoggerFactory loggerFactory ? loggerFactory : null,
            services: _serviceProvider);

    private AgentSessionState DeserializeSessionState(string? sessionStateJson)
    {
        var parsed = string.IsNullOrWhiteSpace(sessionStateJson)
            ? null
            : JsonSerializer.Deserialize<AgentSessionState>(sessionStateJson);
        return _chatModelCatalog.FindModel(parsed?.ModelId) is { } selectedModel
            ? new AgentSessionState(selectedModel.Id)
            : new AgentSessionState(_chatModelCatalog.GetDefaultModel().Id);
    }

    private static string BuildMemoryPrompt(IEnumerable<string> recalledMemories) =>
        "Use these remembered user facts if they are relevant to the current request. Treat them as higher-priority personal memory unless the user corrects them:\n"
        + string.Join("\n", recalledMemories.Select((memory, index) => $"{index + 1}. {memory}"));
}
