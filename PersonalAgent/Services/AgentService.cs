using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, List<ConversationMessage>> _messageHistory = new();

    public AgentService(IOptions<ApiKeyOptions> apiKeyOptions)
    {
        var apiKey = apiKeyOptions.Value.OpenAiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenAI API key not found. In production set OPENAI_API_KEY env var; for local dev use: dotnet user-secrets set OpenApiKey \"your-key\" --project PersonalAgent");

        _agent = new OpenAIClient(apiKey)
            .GetChatClient("gpt-4o-mini")
            .AsAIAgent(
                instructions: "You are a helpful personal assistant. You help with tasks, answer questions, and provide information. Be friendly, concise, and helpful.",
                name: "PersonalAgent");
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
}
