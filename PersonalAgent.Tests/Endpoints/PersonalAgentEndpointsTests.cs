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
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PersonalAgent.Tests.Endpoints;

public class PersonalAgentEndpointsTests
{
    [Theory]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5.6-luna")]
    [InlineData("gpt-5.6-terra")]
    [InlineData("gpt-5.6-sol")]
    [InlineData("gpt-6-astra")]
    public async Task ShippedPolicy_AllowsNewSessionWithAvailableNewerModel(string ModelId)
    {
        var Policy = DatabaseChatModelPolicy.LoadSeed();
        var Source = new Mock<IChatModelDiscovery>();
        Source.Setup(Value => Value.GetModelIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([ModelId, "text-embedding-3-small"]);
        using var Catalog = new ChatModelCatalog(Options.Create(Policy), Source.Object, TimeProvider.System, NullLogger<ChatModelCatalog>.Instance);
        await using var App = await BuildAppAsync(modelCatalog: Catalog);
        var Client = App.GetTestClient();
        var Models = (await ReadJsonAsync(await Client.GetAsync("/api/models"))).GetProperty("models");
        Models.GetArrayLength().Should().Be(1);
        Models[0].GetProperty("id").GetString().Should().Be(ModelId);
        var Response = await Client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user", modelId = ModelId });
        Response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(Response)).GetProperty("modelId").GetString().Should().Be(ModelId);
    }

    [Theory]
    [InlineData("transcript")]
    [InlineData("transcript.txt")]
    public async Task TranscriptReads_RejectOtherOwnerSubject(string Suffix)
    {
        await using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        (await client.GetAsync($"/api/coach-checkins/{Guid.NewGuid()}/{Suffix}?profileId=other-owner")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

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
    public async Task CreateSession_ReturnsUnauthorized_WhenSignedActorMissing()
    {
        await using var app = await BuildAppAsync(addSignedActor: false);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user", modelId = "gpt-4o-mini" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task ScheduleNotificationRequiresSignedActorAndOwnSubject(bool Signed, HttpStatusCode Expected)
    {
        await using var app = await BuildAppAsync(addSignedActor: Signed);
        var response = await app.GetTestClient().PostAsJsonAsync("/api/schedule/notifications", new
        {
            tenantId = "default", userId = "someone-else", title = "Reminder", body = "Body", delay = "PT5M"
        });
        response.StatusCode.Should().Be(Expected);
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
    public async Task ScheduleAgentTaskToolAsync_ReturnsError_WhenNoTimingInputProvided()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleAgentTaskToolAsync(
            access: new("test-user", "Owner", "test-user"),
            instruction: "do the thing");

        result.Should().Contain("provide exactly one of");
    }

    [Fact]
    public async Task ScheduleAgentTaskToolAsync_ReturnsError_WhenMultipleTimingInputsProvided()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleAgentTaskToolAsync(
            access: new("test-user", "Owner", "test-user"),
            instruction: "do the thing",
            delay: "PT5M",
            when: "tonight");

        result.Should().Contain("provide exactly one of");
    }

    [Fact]
    public async Task ScheduleAgentTaskToolAsync_ReturnsError_WhenProfileIdMissing()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleAgentTaskToolAsync(
            access: new("test-user", "Owner", ""),
            instruction: "do the thing",
            delay: "PT5M");

        result.Should().Contain("profileId is required");
    }

    [Fact]
    public async Task ScheduleAgentTaskToolAsync_ReturnsError_WhenInstructionMissing()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleAgentTaskToolAsync(
            access: new("test-user", "Owner", "test-user"),
            instruction: "",
            delay: "PT5M");

        result.Should().Contain("instruction is required");
    }

    [Fact]
    public async Task CoachCheckinsUpload_ReturnsBadRequest_WhenRequestIsNotMultipart()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/api/coach-checkins/uploads", new { profileId = "test-user" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CoachCheckinsStatus_ReturnsBadRequest_WhenProfileIdMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/coach-checkins/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CoachCheckinsSpeakerOverrides_ReturnsBadRequest_WhenOverridesMissing()
    {
        await using var app = await BuildAppAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync($"/api/coach-checkins/{Guid.NewGuid()}/speaker-overrides", new
        {
            profileId = "test-user",
            overrides = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CoachCheckinsTranscript_RequiresInternalApiKey_WhenConfigured()
    {
        await using var app = await BuildAppAsync(internalApiKey: "test-key");
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/coach-checkins/{Guid.NewGuid()}/transcript");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ScheduleAgentTask_RejectsCrossSubjectEvenWithValidTiming()
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

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

    }

    [Fact]
    public async Task ScheduleNotificationToolAsync_ReturnsError_WhenNoTimingInputProvided()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleNotificationToolAsync(
            access: new AgentAccessContext("test-user", "Owner", "test-user"),
            title: "Test",
            body: "Hello");

        result.Should().Contain("provide exactly one of");
    }

    [Fact]
    public async Task ScheduleNotificationToolAsync_ReturnsError_WhenMultipleTimingInputsProvided()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleNotificationToolAsync(
            access: new AgentAccessContext("test-user", "Owner", "test-user"),
            title: "Test",
            body: "Hello",
            delay: "PT5M",
            when: "tonight");

        result.Should().Contain("provide exactly one of");
    }

    [Fact]
    public async Task ScheduleNotificationToolAsync_ReturnsError_WhenProfileIdMissing()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleNotificationToolAsync(
            access: new AgentAccessContext("test-user", "Owner", ""),
            title: "Test",
            body: "Hello",
            delay: "PT5M");

        result.Should().Contain("profileId is required");
    }

    [Fact]
    public async Task ScheduleNotificationToolAsync_ReturnsError_WhenTitleMissing()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleNotificationToolAsync(
            access: new AgentAccessContext("test-user", "Owner", "test-user"),
            title: "",
            body: "Hello",
            delay: "PT5M");

        result.Should().Contain("title is required");
    }

    [Fact]
    public async Task ScheduleNotificationToolAsync_ReturnsError_WhenBodyMissing()
    {
        await using var app = await BuildAppAsync();
        var eventService = app.Services.GetRequiredService<AgentEventService>();

        var result = await eventService.ScheduleNotificationToolAsync(
            access: new AgentAccessContext("test-user", "Owner", "test-user"),
            title: "Test",
            body: "",
            delay: "PT5M");

        result.Should().Contain("body is required");
    }

    [Fact]
    public async Task RuntimeCatalog_ChangesNewSessions_AndPreservesStoredModel()
    {
        var clock = new CatalogTestClock();
        var source = new Mock<IChatModelDiscovery>();
        source.SetupSequence(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["model-a"])
            .ReturnsAsync(["model-b"]);
        using var catalog = new ChatModelCatalog(Options.Create(new ChatModelCatalogOptions
        {
            Models = [new() { Id = "model-a", DisplayName = "First model", IsDefault = true }, new() { Id = "model-b", DisplayName = "Second model" }]
        }), source.Object, clock, NullLogger<ChatModelCatalog>.Instance);
        await using var app = await BuildAppAsync(modelCatalog: catalog);
        var client = app.GetTestClient();
        var firstResponse = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user" });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = await ReadJsonAsync(firstResponse);
        first.GetProperty("modelId").GetString().Should().Be("model-a");
        var sessionId = first.GetProperty("sessionId").GetString();

        clock.Now = clock.Now.AddMinutes(5);
        var modelsResponse = await client.GetAsync("/api/models");
        modelsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var models = (await ReadJsonAsync(modelsResponse)).GetProperty("models");
        models.GetArrayLength().Should().Be(1);
        models[0].GetProperty("id").GetString().Should().Be("model-b");
        models[0].GetProperty("displayName").GetString().Should().Be("Second model");
        models[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();

        var secondResponse = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user" });
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(secondResponse)).GetProperty("modelId").GetString().Should().Be("model-b");
        var unavailable = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user", modelId = "model-a" });
        unavailable.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var transcript = await client.GetAsync($"/api/sessions/{sessionId}/messages?profileId=test-user");
        transcript.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(transcript)).GetProperty("modelId").GetString().Should().Be("model-a");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EmptyCatalog_ReturnsEmptyList_AndServiceUnavailableForNewSession()
    {
        var source = new Mock<IChatModelDiscovery>();
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var catalog = new ChatModelCatalog(Options.Create(new ChatModelCatalogOptions()), source.Object,
            TimeProvider.System, NullLogger<ChatModelCatalog>.Instance);
        await using var app = await BuildAppAsync(modelCatalog: catalog);
        var client = app.GetTestClient();

        var list = await client.GetAsync("/api/models");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(list)).GetProperty("models").GetArrayLength().Should().Be(0);
        var create = await client.PostAsJsonAsync("/api/sessions", new { profileId = "test-user" });
        create.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        create.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await ReadJsonAsync(create)).GetProperty("title").GetString().Should().Be("No chat models are available");
    }

    private sealed class CatalogTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }

    private static async Task<WebApplication> BuildAppAsync(string? internalApiKey = null, bool addSignedActor = true, IChatModelCatalog? modelCatalog = null)
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
            ActorSigningKey = "test-actor-signing-key",
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
            DiscoverFromProvider = false,
            Models = [new ChatModelOption { Id = "gpt-4o-mini", DisplayName = "GPT-4o mini", IsDefault = true }]
        }));
        builder.Services.AddSingleton<IOptions<CoachCheckinOptions>>(Options.Create(new CoachCheckinOptions()));
        builder.Services.AddSingleton<IOptions<AgentMemoryOptions>>(Options.Create(new AgentMemoryOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        }));

        builder.Services.AddSingleton<IOptions<MassTransit.SqlTransportOptions>>(Options.Create(new MassTransit.SqlTransportOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        }));

        builder.Services.AddSingleton<IChatModelCatalog>(sp => modelCatalog ?? ChatModelCatalogFactory(sp));
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
        builder.Services.AddSingleton<IToolAccessStore, TestToolAccessStore>();
        builder.Services.AddSingleton<IAgentToolRegistry, AgentToolRegistry>();
        builder.Services.AddSingleton<AgentToolBinder>();
        builder.Services.AddSingleton<ToolAccessService>();
        builder.Services.AddSingleton<ICoachAssignmentStore, TestCoachAssignmentStore>();
        builder.Services.AddSingleton<PushNotificationService>();
        builder.Services.AddSingleton<IOptions<PushNotificationsOptions>>(Options.Create(new PushNotificationsOptions()));
        builder.Services.AddSingleton<CoachCheckinService>();
        builder.Services.AddSingleton<ICoachEvidenceService, CoachEvidenceService>();
        builder.Services.AddSingleton<AgentChatService>();
        builder.Services.AddSingleton<AgentService>();
        builder.Services.AddSingleton(_ => Mock.Of<IBus>());
        builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var app = builder.Build();
        app.UseRateLimiter();
        if (addSignedActor) app.Use(async (context, next) =>
        {
            const string actorId = "test-user";
            const string role = AgentRoles.Owner;
            const string email = "owner@example.com";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var payload = $"{actorId}\n{role}\n{email}\n{timestamp}";
            var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("test-actor-signing-key"), Encoding.UTF8.GetBytes(payload)));
            context.Request.Headers["X-Agent-Actor"] = actorId;
            context.Request.Headers["X-Agent-Role"] = role;
            context.Request.Headers["X-Agent-Email"] = email;
            context.Request.Headers["X-Agent-Timestamp"] = timestamp;
            context.Request.Headers["X-Agent-Signature"] = signature;
            await next();
        });
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

    private sealed class TestToolAccessStore : IToolAccessStore
    {
        public Task<IReadOnlyDictionary<string, bool>> GetRolePermissionsAsync(string role, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>());

        public Task SavePermissionsAsync(IReadOnlyList<ToolRolePermission> permissions, string updatedBy, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestCoachAssignmentStore : ICoachAssignmentStore
    {
        public Task<IReadOnlyList<string>> GetAssignedProfilesAsync(string coachActorId, string coachEmail, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<CoachProfileAssignment>> GetAssignmentsAsync(string subjectProfileId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CoachProfileAssignment>>([]);

        public Task SaveAssignmentAsync(SaveCoachProfileAssignmentRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ChatModelCatalog ChatModelCatalogFactory(IServiceProvider sp) =>
        new(sp.GetRequiredService<IOptions<ChatModelCatalogOptions>>(), Mock.Of<IChatModelDiscovery>(), TimeProvider.System, NullLogger<ChatModelCatalog>.Instance);

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
