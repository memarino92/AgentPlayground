using AgentPlayground.Contracts.Messaging.Events;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Endpoints;
using PersonalAgent.Models;
using PersonalAgent.Services;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.Tests.Endpoints;

public class PersonalAgentEndpointsTests
{
    [Fact]
    public async Task GetModels_ReturnsConfiguredModels()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("models").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateSession_ReturnsBadRequest_WhenProfileIdMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sessions", new { profileId = "", modelId = "gpt-4o-mini" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSession_ReturnsSessionId_WhenRequestValid()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user", modelId = "gpt-4o-mini" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("sessionId").GetString().Should().NotBeNullOrWhiteSpace();
        payload.GetProperty("modelId").GetString().Should().Be("gpt-4o-mini");
    }

    [Fact]
    public async Task GetSessions_ReturnsBadRequest_WhenProfileIdMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SessionRoutes_RequireInternalApiKey_WhenConfigured()
    {
        await using var app = await BuildAppAsync(internalApiKey: "test-key");
        var client = app.GetTestClient();

        var unauthorized = await client.GetAsync("/api/models");
        unauthorized.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "test-key");
        var authorized = await client.GetAsync("/api/models");
        authorized.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendMessage_ReturnsBadRequest_WhenMessageMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var sessionResponse = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user", modelId = "gpt-4o-mini" });
        var sessionPayload = await ReadJsonAsync(sessionResponse);
        var sessionId = sessionPayload.GetProperty("sessionId").GetString();

        var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", new { profileId = "test-user", message = "" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetMessages_ReturnsBadRequest_WhenProfileIdMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/sessions/{Guid.NewGuid()}/messages");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }

    private static async Task<WebApplication> BuildAppAsync(string? internalApiKey = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter(PersonalAgentConstants.ApiRateLimiter, limiter =>
            {
                limiter.PermitLimit = 100;
                limiter.Window = TimeSpan.FromSeconds(1);
            });
        });

        builder.Services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions
        {
            AllowedOrigins = [],
            InternalApiKey = internalApiKey ?? string.Empty,
            RateLimit = new RateLimitOptions { PermitLimit = 100, WindowSeconds = 1 }
        }));

        builder.Services.AddSingleton<IOptions<ApiKeyOptions>>(Options.Create(new ApiKeyOptions
        {
            OpenAiKey = "test-key",
            InternalApiKey = internalApiKey ?? string.Empty
        }));

        builder.Services.AddSingleton<IOptions<ChatModelCatalogOptions>>(Options.Create(new ChatModelCatalogOptions
        {
            Models = [new ChatModelOption { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", IsDefault = true }]
        }));

        builder.Services.AddSingleton(ChatModelCatalogFactory);
        builder.Services.AddSingleton<IAgentSessionStore, InMemoryAgentSessionStore>();
        builder.Services.AddSingleton<IAgentSemanticMemoryStore>(sp => (InMemoryAgentSessionStore)sp.GetRequiredService<IAgentSessionStore>());
        builder.Services.AddSingleton<IAgentEmbeddingService, TestEmbeddingService>();
        builder.Services.AddSingleton<SemanticMemoryService>();
        builder.Services.AddSingleton<AgentEventService>();
        builder.Services.AddSingleton<AgentChatService>();
        builder.Services.AddSingleton<AgentService>();
        builder.Services.AddSingleton(_ => Mock.Of<IBus>());
        builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapPersonalAgentEndpoints();
        await app.StartAsync();
        return app;
    }

    private static ChatModelCatalog ChatModelCatalogFactory(IServiceProvider sp) =>
        new(sp.GetRequiredService<IOptions<ChatModelCatalogOptions>>());

    private sealed class TestEmbeddingService : IAgentEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 0.1f, 0.2f, 0.3f });
    }

    private sealed class InMemoryAgentSessionStore : IAgentSessionStore, IAgentSemanticMemoryStore
    {
        private readonly Dictionary<Guid, PersistedAgentSession> _sessions = [];
        private readonly Dictionary<Guid, List<ConversationMessage>> _messages = [];

        public Task CreateSessionAsync(Guid sessionId, string profileId, string sessionStateJson, CancellationToken cancellationToken = default)
        {
            _sessions[sessionId] = new PersistedAgentSession(sessionId, profileId, sessionStateJson, 0);
            _messages[sessionId] = [];
            return Task.CompletedTask;
        }

        public Task<PersistedAgentSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);

        public Task<IReadOnlyList<PersistedAgentSessionSummary>> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize, CancellationToken cancellationToken = default)
        {
            var sessions = _sessions.Values
                .Where(session => string.Equals(session.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                .Select(session => new PersistedAgentSessionSummary(session.SessionId, "New chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
                .Take(pageSize)
                .ToList();

            return Task.FromResult<IReadOnlyList<PersistedAgentSessionSummary>>(sessions);
        }

        public Task<bool> SaveInteractionAsync(Guid sessionId, string userMessage, string assistantMessage, string sessionStateJson, CancellationToken cancellationToken = default)
        {
            if (!_sessions.ContainsKey(sessionId)) return Task.FromResult(false);

            _messages[sessionId].Add(new ConversationMessage("user", userMessage));
            _messages[sessionId].Add(new ConversationMessage("assistant", assistantMessage));
            return Task.FromResult(true);
        }

        public Task<List<ConversationMessage>?> GetSessionMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_messages.TryGetValue(sessionId, out var messages) ? messages : null);

        public Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_sessions.ContainsKey(sessionId));

        public Task AddMemoryAsync(Guid sessionId, string profileId, string memoryKind, string content, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<List<MemoryRecord>> SearchMemoriesAsync(string profileId, ReadOnlyMemory<float> embedding, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<MemoryRecord>());
    }
}
