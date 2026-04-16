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
using System.Text;

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
    private readonly ITavilyMcpToolProvider _tavilyMcpToolProvider;
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
        ITavilyMcpToolProvider tavilyMcpToolProvider,
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
        _tavilyMcpToolProvider = tavilyMcpToolProvider;
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

    public async Task<(string Response, string ModelId)?> SendMessageAsync(string sessionId, string profileId, string message, string? modelId = null)
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

            var persistedState = DeserializeSessionState(persistedSession.SessionStateJson);
            var requestedModel = string.IsNullOrWhiteSpace(modelId)
                ? null
                : _chatModelCatalog.FindModel(modelId) ?? throw new InvalidOperationException($"Chat model '{modelId}' is not available.");
            var selectedModelId = requestedModel?.Id ?? persistedState.ModelId;
            var updatedSessionStateJson = JsonSerializer.Serialize(new AgentSessionState(selectedModelId));
            var agent = GetSessionAgent(selectedModelId);
            var session = await agent.CreateSessionAsync();
            var transcript = await _sessionStore.GetSessionMessagesAsync(parsedSessionId) ?? [];
            var recalledMemories = await _semanticMemoryService.RecallMemoriesAsync(persistedSession.ProfileId, message);
            var messages = transcript
                .Select(ToChatMessage)
                .ToList();

            if (recalledMemories.Count > 0)
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, BuildMemoryPrompt(recalledMemories)));

            messages.Add(new Microsoft.Extensions.AI.ChatMessage(
                ChatRole.System,
                $"Current user profileId is '{persistedSession.ProfileId}'. Use this exact value when a tool requires profileId."));

            messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, message));

            var response = await agent.RunAsync(messages, session, options: null, cancellationToken: default);
            var responseText = response.ToString();
            var wasSaved = await _sessionStore.SaveInteractionAsync(parsedSessionId, message, responseText, updatedSessionStateJson);
            var citedUrlCount = CountUrls(responseText);

            _logger.LogInformation(
                "Processed message for model {ModelId}; recalledMemories={RecalledMemories}; persisted={WasSaved}; responseLength={ResponseLength}; citedUrlCount={CitedUrlCount}; tavilyAvailable={TavilyAvailable}",
                selectedModelId,
                recalledMemories.Count,
                wasSaved,
                responseText.Length,
                citedUrlCount,
                _tavilyMcpToolProvider.IsAvailable);

            if (wasSaved)
                await _semanticMemoryService.StoreConversationMemoriesAsync(parsedSessionId, persistedSession.ProfileId, message, responseText);

            return wasSaved ? (responseText, selectedModelId) : null;
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
        var publishTool = WrapTool(AIFunctionFactory.Create(_eventService.PublishGeneratedMessageToolAsync, "publish_generated_test_message",
            "Publish a generated test message to the shared MassTransit bus."));
        var mobileNotifyTool = WrapTool(AIFunctionFactory.Create(_eventService.PublishMobileNotificationToolAsync, "publish_mobile_notification",
            "Send a push notification event to a user's registered mobile device. Use this when the user asks to notify or ping their phone."));
        var syncJournalTool = WrapTool(AIFunctionFactory.Create(_workJournalService.SyncWorkJournalAsync, "sync_work_journal",
            "Trigger a background process to sync the work journal from GitHub. This syncs markdown files and prepares them for semantic search."));
        var searchJournalTool = WrapTool(AIFunctionFactory.Create(_workJournalService.SearchWorkJournalAsync, "search_work_journal",
            "Search the work journal for answers to user questions using RAG (Retrieval-Augmented Generation). Use this tool whenever the user asks about past work, journal entries, or questions like 'when did I work on...' or 'who did I help'."));
        var scheduleNotificationTool = WrapTool(AIFunctionFactory.Create(_eventService.ScheduleNotificationToolAsync, "schedule_notification",
            "Schedule a mobile notification for a user profile using delay (e.g. PT5M), absolute executeAt datetime, or natural when text like 'tonight'."));
        var scheduleAgentTaskTool = WrapTool(AIFunctionFactory.Create(_eventService.ScheduleAgentTaskToolAsync, "schedule_agent_task",
            "Schedule a future agent task for a user profile. Required: profileId, instruction, and exactly one timing field (delay, executeAt, or when). Use notifyOnCompletion to request follow-up notifications."));
        var getCurrentDateTimeTool = WrapTool(AIFunctionFactory.Create(_eventService.GetCurrentDateTimeToolAsync, "get_current_date_time",
            "Get the current date and time, optionally in a specific IANA or Windows timezone (e.g. 'America/Chicago' or 'Central Standard Time'). Call this before scheduling when the user specifies relative times like 'at noon today', '10 PM tomorrow', or 'next Monday'."));
        var webTools = _tavilyMcpToolProvider.GetTools().Select(WrapWebTool).ToList();
        var tools = new List<AIFunction> { publishTool, mobileNotifyTool, syncJournalTool, searchJournalTool, scheduleNotificationTool, scheduleAgentTaskTool, getCurrentDateTimeTool };
        if (webTools.Count > 0) tools.AddRange(webTools);

        _logger.LogInformation(
            "Creating agent for model {ModelId}; tavilyAvailable={TavilyAvailable}; tavilyStatus={TavilyStatus}; tavilyToolCount={TavilyToolCount}; totalToolCount={TotalToolCount}",
            modelId,
            _tavilyMcpToolProvider.IsAvailable,
            _tavilyMcpToolProvider.Status,
            webTools.Count,
            tools.Count);

        if (webTools.Count > 0)
            _logger.LogDebug("Tavily tools for model {ModelId}: {ToolNames}", modelId, string.Join(",", webTools.Select(tool => tool.Name)));

        var instructions = new StringBuilder(
            """
            You are a helpful personal assistant.
            When asked to respond to a bus test event, you must call the publish_generated_test_message tool exactly once with a concise generated message describing that you received the event.
            The tool arguments must include a valid correlationId GUID string copied from context.
            This does not mean that you should respond to every user message with a bus event follow-up, only when you are specifically asked to generate a follow-up message for a bus event.

            When the user asks you to send a notification to their mobile device, call the publish_mobile_notification tool once with their profileId, a short title, and concise body text.

            When the user asks for a reminder later (for example "in 5 minutes" or "at 6pm"), call schedule_notification with profileId, title, body, and exactly one of: delay (ISO-8601 duration like PT5M), executeAt (ISO-8601 datetime), or when (natural text like tonight).

            When the user asks you to do work later and then notify them, call schedule_agent_task with profileId, instruction, and exactly one of: delay (ISO-8601 duration like PT5M), executeAt (ISO-8601 datetime), or when (natural text like tonight).

            Before using executeAt or when for either scheduling tool, call get_current_date_time to determine the current date and time so you can accurately resolve relative times like "at noon today" or "10 PM tomorrow".

            When asked about past work, past events, or anything related to the user's work journal, use the search_work_journal tool to find relevant information.
            If the user asks to sync, update, or fetch their journal, you MUST call the sync_work_journal tool.
            """);

        if (webTools.Count > 0)
            instructions.AppendLine("Use available Tavily web tools for current events, external facts, and documentation lookups. When you use web tools, include source URLs in your response.");
        else
            instructions.AppendLine("Web search is currently unavailable; answer without web tools and acknowledge limits for current events when needed.");

        return _openAiClient
            .GetChatClient(modelId)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .BuildAIAgent(
                instructions: instructions.ToString(),
                name: "PersonalAgent",
                description: "Personal agent that can publish follow-up messages to the shared event bus and query the user's work journal.",
                tools: [.. tools],
                loggerFactory: _loggerFactory,
                services: _serviceProvider);
    }

    private AIFunction WrapTool(AIFunction tool) => new LoggingAIFunction(tool, _logger, "Local");
    private AIFunction WrapWebTool(AIFunction tool) => new LoggingAIFunction(tool, _logger, "TavilyMcp");

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

    private static int CountUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var count = 0;
        var startIndex = 0;
        while (startIndex < text.Length)
        {
            var httpIndex = text.IndexOf("http://", startIndex, StringComparison.OrdinalIgnoreCase);
            var httpsIndex = text.IndexOf("https://", startIndex, StringComparison.OrdinalIgnoreCase);
            var matchIndex = httpIndex switch
            {
                -1 when httpsIndex >= 0 => httpsIndex,
                >= 0 when httpsIndex == -1 => httpIndex,
                >= 0 when httpsIndex >= 0 => Math.Min(httpIndex, httpsIndex),
                _ => -1
            };

            if (matchIndex < 0) break;
            count++;
            startIndex = matchIndex + 1;
        }

        return count;
    }
}
