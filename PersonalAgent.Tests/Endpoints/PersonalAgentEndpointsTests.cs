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

    [Fact]
    public async Task RegisterDeviceToken_ReturnsBadRequest_WhenPushTokenMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/mobile/devices/register", new
        {
            profileId = "test-user",
            deviceId = "device-1",
            platform = "android",
            pushToken = ""
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ApprovalFlow_CreatesAndCompletesApproval()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var createResponse = await client.PostAsJsonAsync("/api/approvals", new
        {
            profileId = "test-user",
            sessionId = "session-123",
            toolName = "DangerousTool",
            actionSummary = "Run dangerous operation",
            requestedBy = "agent"
        });

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var createPayload = await ReadJsonAsync(createResponse);
        var approvalId = createPayload.GetProperty("approvalId").GetGuid();
        createPayload.GetProperty("status").GetString().Should().Be("pending");

        var decisionResponse = await client.PostAsJsonAsync($"/api/approvals/{approvalId}/decision", new
        {
            profileId = "test-user",
            approved = true,
            decidedBy = "mobile-user",
            reason = "looks good"
        });

        decisionResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var decisionPayload = await ReadJsonAsync(decisionResponse);
        decisionPayload.GetProperty("status").GetString().Should().Be("approved");
        decisionPayload.GetProperty("decidedBy").GetString().Should().Be("mobile-user");
    }

    [Fact]
    public async Task ScheduleNotification_ReturnsBadRequest_WhenNoTimingInputProvided()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/schedule/notifications", new
        {
            tenantId = "tenant-a",
            userId = "user-1",
            title = "Reminder",
            body = "Do the thing"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ScheduleNotification_ReturnsBadRequest_WhenMultipleTimingInputsProvided()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/schedule/notifications", new
        {
            tenantId = "tenant-a",
            userId = "user-1",
            title = "Reminder",
            body = "Do the thing",
            delay = "PT5M",
            when = "tonight"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ScheduleAgentTask_ReturnsBadRequest_WhenNoTimingInputProvided()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/schedule/agent-tasks", new
        {
            tenantId = "tenant-a",
            userId = "user-1",
            instruction = "check service health"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ScheduleAgentTask_ReturnsOk_WhenExactlyOneTimingInputProvided()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/schedule/agent-tasks", new
        {
            tenantId = "tenant-a",
            userId = "user-1",
            instruction = "check service health",
            delay = "PT1M"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadJsonAsync(response);
        payload.GetProperty("id").GetGuid().Should().NotBeEmpty();
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
            InternalApiKey = internalApiKey ?? string.Empty,
            EnableWebSearch = false
        }));

        builder.Services.AddSingleton<IOptions<ChatModelCatalogOptions>>(Options.Create(new ChatModelCatalogOptions
        {
            Models = [new ChatModelOption { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", IsDefault = true }]
        }));

        builder.Services.AddSingleton<IOptions<MassTransit.SqlTransportOptions>>(Options.Create(new MassTransit.SqlTransportOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        }));

        builder.Services.AddSingleton(ChatModelCatalogFactory);
        builder.Services.AddSingleton<IAgentSessionStore, InMemoryAgentSessionStore>();
        builder.Services.AddSingleton<IAgentSemanticMemoryStore>(sp => (InMemoryAgentSessionStore)sp.GetRequiredService<IAgentSessionStore>());
        builder.Services.AddSingleton<IAgentApprovalStore>(sp => (InMemoryAgentSessionStore)sp.GetRequiredService<IAgentSessionStore>());
        builder.Services.AddSingleton<IAgentEmbeddingService, TestEmbeddingService>();
        builder.Services.AddSingleton<SemanticMemoryService>();
        builder.Services.AddSingleton<SchedulingService>();
        builder.Services.AddSingleton<AgentEventService>();
        builder.Services.AddSingleton<AgentApprovalService>();
        builder.Services.AddSingleton<WorkJournalService>();
        builder.Services.AddSingleton<ITavilyMcpToolProvider, TestTavilyMcpToolProvider>();
        builder.Services.AddSingleton<PushNotificationService>();
        builder.Services.AddSingleton<IOptions<PushNotificationsOptions>>(Options.Create(new PushNotificationsOptions()));
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

    private sealed class TestTavilyMcpToolProvider : ITavilyMcpToolProvider
    {
        public bool IsAvailable => false;
        public string Status => "TestStub";
        public IReadOnlyList<Microsoft.Extensions.AI.AIFunction> GetTools() => [];
    }

    private static ChatModelCatalog ChatModelCatalogFactory(IServiceProvider sp) =>
        new(sp.GetRequiredService<IOptions<ChatModelCatalogOptions>>());

    private sealed class TestEmbeddingService : IAgentEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 0.1f, 0.2f, 0.3f });

        public Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> contents, CancellationToken cancellationToken = default) =>
            Task.FromResult(contents.Select(_ => new ReadOnlyMemory<float>(new float[] { 0.1f, 0.2f, 0.3f })).ToList());
    }

    private sealed class InMemoryAgentSessionStore : IAgentSessionStore, IAgentSemanticMemoryStore, IAgentApprovalStore
    {
        private readonly Dictionary<Guid, PersistedAgentSession> _sessions = [];
        private readonly Dictionary<Guid, List<ConversationMessage>> _messages = [];
        private readonly Dictionary<string, List<PersistedMobileDeviceToken>> _deviceTokens = [];
        private readonly Dictionary<Guid, PersistedAgentApproval> _approvals = [];

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

        public Task RegisterMobileDeviceTokenAsync(string profileId, string deviceId, string platform, string pushToken, string? appVersion, CancellationToken cancellationToken = default)
        {
            if (!_deviceTokens.TryGetValue(profileId, out var tokens))
            {
                tokens = [];
                _deviceTokens[profileId] = tokens;
            }

            var now = DateTimeOffset.UtcNow;
            tokens.RemoveAll(t => string.Equals(t.DeviceId, deviceId, StringComparison.Ordinal));
            tokens.Add(new PersistedMobileDeviceToken(deviceId, platform, pushToken, appVersion, now, now));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PersistedMobileDeviceToken>> GetMobileDeviceTokensAsync(string profileId, CancellationToken cancellationToken = default)
        {
            var tokens = _deviceTokens.TryGetValue(profileId, out var stored)
                ? (IReadOnlyList<PersistedMobileDeviceToken>)stored
                : [];

            return Task.FromResult(tokens);
        }

        public Task<PersistedAgentApproval> CreateAgentApprovalAsync(string profileId, string sessionId, string toolName, string actionSummary, string requestedBy, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            var approval = new PersistedAgentApproval(
                Guid.NewGuid(),
                profileId,
                sessionId,
                toolName,
                actionSummary,
                requestedBy,
                DateTimeOffset.UtcNow,
                expiresAt,
                "pending",
                null,
                null,
                null);
            _approvals[approval.ApprovalId] = approval;
            return Task.FromResult(approval);
        }

        public Task<PersistedAgentApproval?> CompleteAgentApprovalAsync(Guid approvalId, string profileId, bool approved, string decidedBy, string? reason, CancellationToken cancellationToken = default)
        {
            if (!_approvals.TryGetValue(approvalId, out var existing))
                return Task.FromResult<PersistedAgentApproval?>(null);

            if (!string.Equals(existing.ProfileId, profileId, StringComparison.Ordinal) || !string.Equals(existing.Status, "pending", StringComparison.Ordinal))
                return Task.FromResult<PersistedAgentApproval?>(null);

            var updated = existing with
            {
                Status = approved ? "approved" : "denied",
                DecisionAt = DateTimeOffset.UtcNow,
                DecidedBy = decidedBy,
                Reason = reason
            };
            _approvals[approvalId] = updated;
            return Task.FromResult<PersistedAgentApproval?>(updated);
        }

        public Task<PersistedAgentApproval?> GetAgentApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_approvals.TryGetValue(approvalId, out var approval) ? approval : null);
    }
}
