using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;

namespace PersonalAgent.Services;

internal class AgentChatService
{
    private readonly IChatModelCatalog _chatModelCatalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IAgentSessionStore _sessionStore;
    private readonly SemanticMemoryService _semanticMemoryService;
    private readonly IAgentChatClientFactory _chatClients;
    private readonly AgentToolBinder _toolBinder;
    private readonly ILogger<AgentChatService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public AgentChatService(
        IOptions<ApiKeyOptions> apiKeyOptions,
        IChatModelCatalog chatModelCatalog,
        ILoggerFactory loggerFactory,
        IServiceProvider serviceProvider,
        IAgentSessionStore sessionStore,
        SemanticMemoryService semanticMemoryService,
        AgentToolBinder toolBinder,
        ILogger<AgentChatService> logger,
        IAgentChatClientFactory? chatClients = null)
    {
        _chatClients = chatClients ?? new OpenAiAgentChatClientFactory(apiKeyOptions);
        _chatModelCatalog = chatModelCatalog;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _sessionStore = sessionStore;
        _semanticMemoryService = semanticMemoryService;
        _toolBinder = toolBinder;
        _logger = logger;
    }

    public async Task<(string SessionId, string ModelId)> CreateSessionAsync(string profileId, string modelId)
        => await CreateSessionAsync(new AgentAccessContext(profileId, AgentRoles.Owner, profileId), modelId);

    public async Task<(string SessionId, string ModelId)> CreateSessionAsync(AgentAccessContext access, string modelId, CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);

        var selectedModel = await _chatModelCatalog.FindModelAsync(modelId, cancellationToken)
            ?? throw new InvalidOperationException($"Chat model '{modelId}' is not available.");
        return await CreateSessionAsync(access, selectedModel, cancellationToken);
    }

    // The endpoint passes the selection from its catalog snapshot, avoiding a second discovery after authorization.
    public async Task<(string SessionId, string ModelId)> CreateSessionAsync(AgentAccessContext access, AvailableChatModel selectedModel, CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        var sessionId = Guid.NewGuid();
        var sessionState = JsonSerializer.Serialize(new AgentSessionState(selectedModel.Id));
        await _sessionStore.CreateSessionAsync(sessionId, access, sessionState, cancellationToken);
        _logger.LogInformation(
            "Created session {SessionId} for actor {ActorId}, role {Role}, subject {SubjectProfileId} using model {ModelId}",
            sessionId,
            access.ActorId,
            access.Role,
            access.SubjectProfileId,
            selectedModel.Id);
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

    public Task<SessionSummaryPage> GetSessionsAsync(AgentAccessContext access, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize)
    {
        ValidateAccess(access);
        return GetSessionsAsync(access.ActorId, beforeActivityAt, beforeSessionId, pageSize);
    }

    public async Task<string?> SendMessageAsync(string sessionId, string profileId, string message)
        => await SendMessageAsync(sessionId, new AgentAccessContext(profileId, AgentRoles.Owner, profileId), message);

    public async Task<string?> SendMessageAsync(string sessionId, AgentAccessContext access, string message, CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (!Guid.TryParse(sessionId, out var parsedSessionId))
            return null;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = sessionId,
            ["ActorId"] = access.ActorId,
            ["Role"] = access.Role,
            ["SubjectProfileId"] = access.SubjectProfileId
        });

        var normalizedSessionId = parsedSessionId.ToString();
        var sessionLock = _sessionLocks.GetOrAdd(normalizedSessionId, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);

        try
        {
            var persistedSession = await _sessionStore.GetSessionAsync(parsedSessionId);
            if (persistedSession is null
                || !string.Equals(persistedSession.EffectiveActorId, access.ActorId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(persistedSession.ProfileId, access.SubjectProfileId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Session not found or profile mismatch while sending message");
                return null;
            }

            var sessionState = await DeserializeSessionStateAsync(persistedSession.SessionStateJson, cancellationToken);
            var agent = await CreateSessionAgentAsync(sessionState.ModelId, access, cancellationToken);
            var session = await agent.CreateSessionAsync(cancellationToken);
            var transcript = await _sessionStore.GetSessionMessagesAsync(parsedSessionId) ?? [];
            var recalledMemories = await _semanticMemoryService.RecallMemoriesAsync(persistedSession.EffectiveMemoryProfileId, message, cancellationToken);
            var messages = transcript
                .Select(ToChatMessage)
                .ToList();

            if (recalledMemories.Count > 0)
                messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, BuildMemoryPrompt(recalledMemories)));

            messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, $"The current role is {access.Role}. Athlete data access is already scoped by the server."));

            messages.Add(new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, message));

            var response = await agent.RunAsync(messages, session, options: null, cancellationToken);
            var responseText = response.ToString();
            var wasSaved = await _sessionStore.SaveInteractionAsync(parsedSessionId, message, responseText, persistedSession.SessionStateJson);
            var citedUrlCount = CountUrls(responseText);

            _logger.LogInformation(
                "Processed message for model {ModelId}; recalledMemories={RecalledMemories}; persisted={WasSaved}; responseLength={ResponseLength}; citedUrlCount={CitedUrlCount}",
                sessionState.ModelId,
                recalledMemories.Count,
                wasSaved,
                responseText.Length,
                citedUrlCount);

            if (wasSaved)
                await _semanticMemoryService.StoreConversationMemoriesAsync(parsedSessionId, persistedSession.EffectiveMemoryProfileId, message, responseText, cancellationToken);

            return wasSaved ? responseText : null;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<SessionConversation?> GetSessionMessagesAsync(string sessionId, string profileId)
        => await GetSessionMessagesAsync(sessionId, new AgentAccessContext(profileId, AgentRoles.Owner, profileId));

    public async Task<SessionConversation?> GetSessionMessagesAsync(string sessionId, AgentAccessContext access, CancellationToken cancellationToken = default)
    {
        ValidateAccess(access);
        if (!Guid.TryParse(sessionId, out var parsedSessionId)) return null;

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["SessionId"] = sessionId,
            ["ActorId"] = access.ActorId,
            ["Role"] = access.Role,
            ["SubjectProfileId"] = access.SubjectProfileId
        });

        var persistedSession = await _sessionStore.GetSessionAsync(parsedSessionId);
        if (persistedSession is null
            || !string.Equals(persistedSession.EffectiveActorId, access.ActorId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(persistedSession.ProfileId, access.SubjectProfileId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Session not found or profile mismatch while loading transcript");
            return null;
        }

        var messages = await _sessionStore.GetSessionMessagesAsync(parsedSessionId);
        if (messages is null) return null;

        var sessionState = await DeserializeSessionStateAsync(persistedSession.SessionStateJson, cancellationToken);
        _logger.LogInformation("Loaded transcript containing {MessageCount} messages using model {ModelId}", messages.Count, sessionState.ModelId);
        return new SessionConversation(sessionId, sessionState.ModelId, messages);
    }

    private static Microsoft.Extensions.AI.ChatMessage ToChatMessage(ConversationMessage message) => new(message.Role switch
    {
        "assistant" => ChatRole.Assistant,
        _ => ChatRole.User
    }, message.Content);

    private async Task<ChatClientAgent> CreateSessionAgentAsync(string modelId, AgentAccessContext access, CancellationToken cancellationToken)
    {
        var tools = await _toolBinder.BindAsync(access, cancellationToken);
        var webToolCount = tools.Count(Tool => Tool.Source == "TavilyMcp");
        _logger.LogInformation("Creating agent for model {ModelId} with {ToolCount} authorized tools, including {WebToolCount} web tools",
            modelId, tools.Count, webToolCount);

        var instructions = new StringBuilder(
            """
            You are a helpful personal assistant.
            When the user asks you to send a notification to their mobile device, call the publish_mobile_notification tool once with a short title and concise body text.

            When the user asks for a reminder later (for example "in 5 minutes" or "at 6pm"), call schedule_notification with title, body, and exactly one timing input.

            When the user asks you to do work later and then notify them, call schedule_agent_task with instruction and exactly one timing input.

            Before using executeAt or when for either scheduling tool, call get_current_date_time to determine the current date and time so you can accurately resolve relative times like "at noon today" or "10 PM tomorrow".

            When asked about past work, past events, or anything related to the user's work journal, use the search_work_journal tool to find relevant information.
            If the user asks to sync, update, or fetch their journal, you MUST call the sync_work_journal tool.

            When the user asks about strongman coaching calls, cues by exercise, or prior check-in guidance, use search_coach_checkins. Athlete scope is applied by the server.
            """);

        if (webToolCount > 0)
            instructions.AppendLine("Use available Tavily web tools for current events, external facts, and documentation lookups. When you use web tools, include source URLs in your response.");
        else
            instructions.AppendLine("Web search is currently unavailable; answer without web tools and acknowledge limits for current events when needed.");

        return _chatClients.Create(modelId)
            .AsBuilder()
            .UseFunctionInvocation()
            .BuildAIAgent(
                instructions: instructions.ToString(),
                name: "PersonalAgent",
                description: "Personal agent that can publish follow-up messages to the shared event bus and query the user's work journal.",
                tools: [.. tools.Select(Tool => Tool.Function)],
                loggerFactory: _loggerFactory,
                services: _serviceProvider);
    }

    private static void ValidateAccess(AgentAccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.ActorId)) throw new InvalidOperationException("Actor id is required.");
        if (!AgentRoles.IsDefined(access.Role)) throw new InvalidOperationException("A defined role is required.");
        if (string.IsNullOrWhiteSpace(access.SubjectProfileId)) throw new InvalidOperationException("Subject profile id is required.");
    }

    private async Task<AgentSessionState> DeserializeSessionStateAsync(string? sessionStateJson, CancellationToken cancellationToken)
    {
        var parsed = string.IsNullOrWhiteSpace(sessionStateJson)
            ? null
            : JsonSerializer.Deserialize<AgentSessionState>(sessionStateJson);
        // Inventory changes apply to new sessions. Never silently retarget an existing conversation.
        return !string.IsNullOrWhiteSpace(parsed?.ModelId)
            ? parsed
            : new AgentSessionState((await _chatModelCatalog.GetDefaultModelAsync(cancellationToken)).Id);
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
