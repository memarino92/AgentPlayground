using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

using PersonalAgent.Api.Coding;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CodingSubscriptionGatewayTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer Database = new PostgreSqlBuilder("postgres:18").Build();
    private readonly Guid Connection = Guid.NewGuid();
    private readonly string Capability = new('a', 64);
    private readonly Dictionary<string, bool> Grants = new() { [CodingJobs.Permission] = true };
    private CodingJobStore Jobs = null!;
    private ChatGptModelCatalog Models = null!;
    private CodingModelGateway Gateway = null!;
    private Transport Handler = null!;
    private HttpClient Http = null!;
    private CodingSettings Settings = null!;

    public async Task InitializeAsync()
    {
        await Database.StartAsync();
        var Integration = new IntegrationDatabase(Database.GetConnectionString(), Convert.ToBase64String(new byte[32]));
        Jobs = new(Integration);
        await Jobs.InitializeAsync(default);
        await using var Db = await Integration.OpenAsync(default);
        await using var Schema = new NpgsqlCommand("""
            CREATE TABLE app.configuration_settings(scope text, key text, value text, is_secret boolean, is_active boolean, PRIMARY KEY(scope,key));
            """, Db);
        await Schema.ExecuteNonQueryAsync();
        Handler = new(); Http = new(Handler);
        var Connections = new ChatGptConnectionStore(Integration, new ChatGptOAuth(Http));
        await Connections.InitializeAsync(default);
        var Credential = new ChatGptCredentials("oaiapp_test", "subject", null, "", "subscription-token", "refresh", ChatGptOAuth.Scope, DateTimeOffset.UtcNow.AddHours(1));
        await using var Seed = new NpgsqlCommand("""
            INSERT INTO app.chatgpt_connections(id,actor_id,client_id,subject,credentials) VALUES(@id,'owner','oaiapp_test','subject',@credentials)
            """, Db);
        Seed.Parameters.AddWithValue("id", Connection);
        Seed.Parameters.AddWithValue("credentials", PostgresConfigurationCrypto.Encrypt(JsonSerializer.Serialize(Credential), "ChatGPT", Connection.ToString(), Integration.EncryptionKey));
        await Seed.ExecuteNonQueryAsync();
        Settings = new() { Enabled = true, ModelId = ChatGptModelIds.Create(Connection, "gpt-6-astra"), MaxModelRequests = 500,
            ModelBudgetUsd = 0, Checkpoint = "coding-subscription", ImageId = "sha256:" + new string('a', 64), GitHubAppId = 1, InstallationId = 1 };
        using var Rsa = RSA.Create(2048);
        await Jobs.SaveSettingsAsync(new(0, Settings, GitHubPrivateKeyAction: "replace", GitHubPrivateKey: Rsa.ExportRSAPrivateKeyPem()), default);
        var Global = new Mock<IChatModelCatalog>();
        Global.Setup(Catalog => Catalog.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AvailableChatModel>());
        var Factory = new Mock<IHttpClientFactory>();
        Factory.Setup(Value => Value.CreateClient("ChatGPT")).Returns(Http);
        Models = new(Connections, Factory.Object, Global.Object, NullLogger<ChatGptModelCatalog>.Instance);
        var Policy = new Mock<IScheduledActorPolicy>();
        Policy.Setup(Value => Value.ResolveRoleAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(AgentRoles.Owner);
        var AccessStore = new Mock<IToolAccessStore>();
        AccessStore.Setup(Value => Value.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Grants);
        var Registry = new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(Provider => Provider.GetTools() == Array.Empty<AIFunction>()));
        var Access = new ToolAccessService(AccessStore.Object, Registry);
        var Service = new CodingJobService(Jobs, new(Policy.Object, Mock.Of<ICoachAssignmentStore>(), Access), Access, Mock.Of<ICodingPublisher>(), Models);
        Gateway = new(Jobs, Service, Http, Connections, Models, NullLogger<CodingModelGateway>.Instance);
    }

    public async Task DisposeAsync() { Http.Dispose(); await Database.DisposeAsync(); }

    private async Task<CodingJob> CreateAsync(string Actor = "owner", int Limit = 500)
    {
        var Job = new CodingJob { Id = Guid.NewGuid(), ActorId = Actor, Subject = Actor, Settings = Settings with { MaxModelRequests = Limit },
            CreatedAt = DateTimeOffset.UtcNow, Deadline = DateTimeOffset.UtcNow.AddMinutes(20) };
        await Jobs.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        (await Jobs.ClaimAsync(Job.Id, Capability, "controller-secret", "environment", default)).Should().BeTrue();
        return Job;
    }

    private DefaultHttpContext Context(string? Cap = null)
    {
        var Context = new DefaultHttpContext();
        Context.Request.Headers.Authorization = "Bearer " + (Cap ?? Capability);
        Context.Request.ContentType = "application/json";
        Context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"gpt-6-astra","input":[{"role":"user","content":"Implement a feature"}]}"""));
        Context.Response.Body = new MemoryStream();
        return Context;
    }

    [Fact]
    public async Task DispatchUsesOwnersOAuthAndSnapshotsModelWithoutRouterKey()
    {
        var Job = await CreateAsync();
        await Gateway.RelayAsync(Job.Id, Context(), default, true);
        Handler.Inferences.Should().Be(1);
        (await Jobs.GetAsync(Job.Id, default))!.ModelRequests.Should().Be(1);
        (await Jobs.SettingsAsync(default)).View.HasOpenRouterKey.Should().BeFalse();
        (await Models.GetModelsAsync("other-owner", default)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("owner")]
    [InlineData("permission")]
    [InlineData("policy")]
    [InlineData("connection")]
    public async Task RevokedScopeOrForeignAccountNeverDispatches(string Failure)
    {
        var Job = await CreateAsync(Failure == "owner" ? "other-owner" : "owner");
        if (Failure == "permission") Grants[CodingJobs.Permission] = false;
        if (Failure is "policy" or "connection")
        {
            await using var Db = new NpgsqlConnection(Database.GetConnectionString()); await Db.OpenAsync();
            await using var Command = new NpgsqlCommand(Failure == "policy" ? "UPDATE app.configuration_settings SET value='[]'" : "DELETE FROM app.chatgpt_connections", Db);
            await Command.ExecuteNonQueryAsync();
        }
        await FluentActions.Awaiting(() => Gateway.RelayAsync(Job.Id, Context(Failure == "capability" ? new string('b', 64) : null), default, true))
            .Should().ThrowAsync<Exception>();
        Handler.Inferences.Should().Be(0);
        (await Jobs.GetAsync(Job.Id, default))!.ModelRequests.Should().Be(0);
    }

    [Fact]
    public async Task ExhaustedRequestAllowanceReturnsActionableLimitAndNeverFallsBack()
    {
        var Job = await CreateAsync(Limit: 1);
        await Gateway.RelayAsync(Job.Id, Context(), default, true);
        var Failure = await FluentActions.Awaiting(() => Gateway.RelayAsync(Job.Id, Context(), default, true)).Should().ThrowAsync<CodingGatewayException>();
        Failure.Which.Status.Should().Be(429);
        Failure.Which.Message.Should().Contain("request limit exhausted");
        Handler.Inferences.Should().Be(1);
    }

    [Fact]
    public async Task PlanQuotaFailureDoesNotUsePaidProvider()
    {
        Handler.Status = HttpStatusCode.TooManyRequests;
        var Job = await CreateAsync();
        var Failure = await FluentActions.Awaiting(() => Gateway.RelayAsync(Job.Id, Context(), default, true)).Should().ThrowAsync<CodingGatewayException>();
        Failure.Which.Status.Should().Be(429);
        Failure.Which.Message.Should().Contain("plan limits").And.NotContain("private-provider-body");
        Handler.Inferences.Should().Be(1);
    }

    private sealed class Transport : HttpMessageHandler
    {
        public int Inferences;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Request.RequestUri!.Host.Should().Be("api.openai.com");
            Request.Headers.Authorization!.Parameter.Should().Be("subscription-token");
            if (Request.RequestUri.AbsolutePath == "/v1/models")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { models = new[] { new { slug = "gpt-6-astra", display_name = "Astra", visibility = "list" } } }) };
            Request.RequestUri.AbsolutePath.Should().Be("/v1/responses");
            Inferences++;
            var Body = JsonNode.Parse(await Request.Content!.ReadAsStringAsync(Token))!;
            Body["model"]!.GetValue<string>().Should().Be("gpt-6-astra");
            Body["store"]!.GetValue<bool>().Should().BeFalse();
            Body["stream"]!.GetValue<bool>().Should().BeTrue();
            return new(Status) { Content = new StringContent(Status == HttpStatusCode.OK
                ? "event: response.completed\ndata: {\"type\":\"response.completed\"}\n\n" : "private-provider-body", Encoding.UTF8, "text/event-stream") };
        }
    }
}
