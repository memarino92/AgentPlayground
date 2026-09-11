using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Endpoints;
using PersonalAgent.Security;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Endpoints;

public sealed class IntegrationSettingsEndpointsTests
{
    [Theory]
    [InlineData("other-owner", "Owner", true, false, 403)]
    [InlineData("admin", "Coach", true, false, 403)]
    [InlineData("admin", "Owner", false, false, 401)]
    [InlineData("admin", "Owner", true, true, 401)]
    public async Task EveryRoute_RejectsUnauthorizedActorBeforeCallingService(string Actor, string Role, bool InternalKey, bool ForgeSignature, int Expected)
    {
        var service = new Mock<IIntegrationSettingsService>(MockBehavior.Strict);
        await using var app = await CreateAsync(service.Object);
        foreach (var (method, suffix) in new[] { (HttpMethod.Get, ""), (HttpMethod.Put, ""), (HttpMethod.Post, "/apply"), (HttpMethod.Post, "/reload"), (HttpMethod.Post, "/test") })
        {
            using var client = app.GetTestClient();
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var request = new HttpRequestMessage(method, "/api/admin/integrations/sentry" + suffix);
            if (method != HttpMethod.Get) request.Content = JsonContent.Create(new { expectedRevision = 0, values = new { }, revision = 1 });
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(Expected);
        }
        service.VerifyNoOtherCalls();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Post })
        {
            using var client = app.GetTestClient();
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var request = new HttpRequestMessage(method, "/api/admin/settings" + (method == HttpMethod.Post ? "/reload" : ""));
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new SaveDatabaseSettingsRequest([]));
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(Expected);
        }
    }

    [Fact]
    public async Task AuthorizedSave_UsesSignedActor_AndMapsValidationAndConflicts()
    {
        var service = new Mock<IIntegrationSettingsService>();
        service.Setup(Service => Service.SaveAsync(It.IsAny<SaveIntegrationRequest>(), "admin", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IntegrationValidationException(new() { ["Dsn"] = ["Invalid DSN."] }));
        service.Setup(Service => Service.ApplyAsync(3, "admin", It.IsAny<CancellationToken>())).ThrowsAsync(new IntegrationConflictException());
        await using var app = await CreateAsync(service.Object);
        using var client = app.GetTestClient();
        Sign(client, "admin", "Owner", true, false);
        using var invalid = await client.PutAsJsonAsync("/api/admin/integrations/sentry", new { expectedRevision = 0, values = new { Dsn = "private-input" }, actor = "forged" });
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("Invalid DSN").And.NotContain("private-input");
        using var stale = await client.PostAsJsonAsync("/api/admin/integrations/sentry/apply", new ApplyIntegrationRequest(3));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        service.Verify(Service => Service.SaveAsync(It.IsAny<SaveIntegrationRequest>(), "admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<WebApplication> CreateAsync(IIntegrationSettingsService Service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationSettings:AdministratorIds"] = "admin" });
        builder.Services.AddSingleton(Service);
        builder.Services.AddSingleton(new DatabaseSettingsStore(new IntegrationDatabase("", "")));
        builder.Services.AddSingleton<DatabaseCredentialRuntime>();
        var app = builder.Build();
        var group = app.MapGroup("/api").AddEndpointFilter(new InternalApiKeyFilter(Options.Create(new ApiKeyOptions { InternalApiKey = "internal" })));
        group.MapIntegrationSettings(Options.Create(new SecurityOptions { ActorSigningKey = "signing-key" }), builder.Configuration);
        await app.StartAsync();
        return app;
    }

    private static void Sign(HttpClient Client, string Actor, string Role, bool InternalKey, bool ForgeSignature)
    {
        if (InternalKey) Client.DefaultRequestHeaders.Add("X-Internal-Api-Key", "internal");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("signing-key"), Encoding.UTF8.GetBytes($"{Actor}\n{Role}\n\n{timestamp}")));
        Client.DefaultRequestHeaders.Add("X-Agent-Actor", Actor);
        Client.DefaultRequestHeaders.Add("X-Agent-Role", Role);
        Client.DefaultRequestHeaders.Add("X-Agent-Timestamp", timestamp);
        Client.DefaultRequestHeaders.Add("X-Agent-Signature", ForgeSignature ? "invalid" : signature);
    }
}
