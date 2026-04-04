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

internal class AgentChatService
{
    private readonly ChatModelCatalog _chatModelCatalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ChatClientAgent> _sessionAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAgentSessionStore _sessionStore;
    private readonly SemanticMemoryService _semanticMemoryService;
    private readonly OpenAIClient _openAiClient;
    private readonly AgentEventService _eventService;
    private readonly WorkJournalService _workJournalService;
    private readonly ILogger<AgentChatService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public AgentChatService(
        IOptions<ApiKeyOptions> apiKeyOptions,
        ChatModelCatalog chatModelCatalog,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentSessionStore sessionStore,
        SemanticMemoryService semanticMemoryService,
        AgentEventService eventService,
        WorkJournalService workJournalService,
        ILogger<AgentChatService> logger)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        _openAiClient = new OpenAIClient(apiKey);
        _chatModelCatalog = chatModelCatalog;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _semanticMemoryService = semanticMemoryService;
        _eventService = eventService;
        _workJournalService = workJournalService;
        _logger = logger;

        var defaultModelId = _chatModelCatalog.GetDefaultModel().Id;
        _sessionAgents.TryAdd(defaultModelId, CreateSessionAgent(defaultModelId));
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
        _logger.LogInformation("Created session {SessionId} for profile {ProfileId} using model {ModelId}", sessionId, profileId, selectedModel.Id);
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

        _logger.LogInformation("Loaded {SessionCount} sessions for profile {ProfileId}", pageItems.Count, profileId);
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

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = sessionId,
            ["ProfileId"] = profileId
        });

        var sessionLock = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync();

        try
        {
            var persistedSession = await _sessionStore.GetSessionAsync(parsedSessionId);
            if (persistedSession is null || !string.Equals(persistedSession.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Session not found or profile mismatch while sending message");
                return null;
            }

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

            _logger.LogInformation(
                "Processed message for model {ModelId}; recalledMemories={RecalledMemories}; persisted={WasSaved}",
                sessionState.ModelId,
                recalledMemories.Count,
                wasSaved);

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

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = sessionId,
            ["ProfileId"] = profileId
        });

        var persistedSession = await _sessionStore.GetSessionAsync(parsedSessionId);
        if (persistedSession is null || !string.Equals(persistedSession.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Session not found or profile mismatch while loading transcript");
            return null;
        }

        var messages = await _sessionStore.GetSessionMessagesAsync(parsedSessionId);
        if (messages is null) return null;

        var sessionState = DeserializeSessionState(persistedSession.SessionStateJson);
        _logger.LogInformation("Loaded transcript containing {MessageCount} messages using model {ModelId}", messages.Count, sessionState.ModelId);
        return new SessionConversation(sessionId, sessionState.ModelId, messages);
    }

    private static Microsoft.Extensions.AI.ChatMessage ToChatMessage(ConversationMessage message) => new(message.Role switch
    {
        "assistant" => ChatRole.Assistant,
        _ => ChatRole.User
    }, message.Content);

    private ChatClientAgent GetSessionAgent(string modelId) => _sessionAgents.GetOrAdd(modelId, CreateSessionAgent);

    private ChatClientAgent CreateSessionAgent(string modelId)
    {
        var publishTool = AIFunctionFactory.Create(_eventService.PublishGeneratedMessageToolAsync, "publish_generated_test_message",
            "Publish a generated test message to the shared MassTransit bus.");
        var syncJournalTool = AIFunctionFactory.Create(_workJournalService.SyncWorkJournalAsync, "sync_work_journal",
            "Trigger a background process to sync the work journal from GitHub. This syncs markdown files and prepares them for semantic search.");
        var searchJournalTool = AIFunctionFactory.Create(_workJournalService.SearchWorkJournalAsync, "search_work_journal",
            "Search the work journal for answers to user questions using RAG (Retrieval-Augmented Generation). Use this tool whenever the user asks about past work, journal entries, or questions like 'when did I work on...' or 'who did I help'.");

        return _openAiClient
            .GetChatClient(modelId)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .BuildAIAgent(
                instructions: """
                    You are a helpful personal assistant.
                    When asked to respond to a bus test event, you must call the publish_generated_test_message tool exactly once with a concise generated message describing that you received the event.
                    The tool arguments must include a valid correlationId GUID string copied from context.
                    This does not mean that you should respond to every user message with a bus event follow-up, only when you are specifically asked to generate a follow-up message for a bus event.
                    
                    When asked about past work, past events, or anything related to the user's work journal, use the search_work_journal tool to find relevant information.
                    If the user asks to sync, update, or fetch their journal, you MUST call the sync_work_journal tool.
                    """,
                name: "PersonalAgent",
                description: "Personal agent that can publish follow-up messages to the shared event bus and query the user's work journal.",
                tools: [publishTool, syncJournalTool, searchJournalTool],
                loggerFactory: _loggerFactory,
                services: _serviceProvider);
    }

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
