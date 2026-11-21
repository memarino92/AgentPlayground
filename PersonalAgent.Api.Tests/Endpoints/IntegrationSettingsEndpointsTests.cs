using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using PersonalAgent.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Endpoints;
using PersonalAgent.Api.Security;
using PersonalAgent.Api.Services;
using Xunit;

namespace PersonalAgent.Api.Tests.Endpoints;

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
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var client = app.GetTestClient();
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var request = new HttpRequestMessage(method, "/api/admin/automation-runtime");
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new PersonalAgent.Contracts.Automations.SaveAutomationRuntime(0, new()));
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(Expected);
        }
        foreach (var (method, suffix) in new[] { (HttpMethod.Get, ""), (HttpMethod.Put, ""), (HttpMethod.Post, "/apply"), (HttpMethod.Post, "/reload") })
        {
            using var client = app.GetTestClient();
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var request = new HttpRequestMessage(method, "/api/admin/integrations/otel" + suffix);
            if (method != HttpMethod.Get) request.Content = JsonContent.Create(new { expectedRevision = 0, values = new { }, revision = 1 });
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(Expected);
        }
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Post })
        {
            using var client = app.GetTestClient();
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var request = new HttpRequestMessage(method, "/api/admin/settings" + (method == HttpMethod.Post ? "/reload" : ""));
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new SaveDatabaseSettingsRequest([]));
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(Expected);
        }
        using (var client = app.GetTestClient())
        {
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var response = await client.PutAsJsonAsync("/api/admin/settings/providers/openrouter",
                new SaveProviderCredentialRequest(null, "private-key"));
            ((int)response.StatusCode).Should().Be(Expected);
        }
        foreach (var (method, suffix) in new[] { (HttpMethod.Get, ""), (HttpMethod.Put, ""), (HttpMethod.Post, "/test") })
        {
            using var client = app.GetTestClient();
            Sign(client, Actor, Role, InternalKey, ForgeSignature);
            using var request = new HttpRequestMessage(method, "/api/admin/settings/whatbox" + suffix);
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(new { });
            using var response = await client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(Expected);
        }
        foreach (var Method in new[] { HttpMethod.Get, HttpMethod.Post })
        {
            using var Client = app.GetTestClient();
            Sign(Client, Actor, Role, InternalKey, ForgeSignature);
            using var Request = new HttpRequestMessage(Method, $"/api/admin/settings/proposals/{Guid.NewGuid()}" + (Method == HttpMethod.Post ? "/confirm" : ""));
            if (Method == HttpMethod.Post) Request.Content = JsonContent.Create(new ConfirmCoachingSetting(true));
            using var Response = await Client.SendAsync(Request);
            ((int)Response.StatusCode).Should().Be(Expected);
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

    [Fact]
    public async Task ProposalRoute_RequiresSeparateExactConfirmationAndCannotChangeItsPayload()
    {
        var Store = new Mock<IDatabaseSettingsStore>(MockBehavior.Strict);
        Store.Setup(S => S.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new("Api", "CoachCheckins:CoachName", "1", "Old", false, true)]);
        Store.Setup(S => S.SaveAsync(It.Is<SaveDatabaseSettingsRequest>(R => R.Changes.Count == 1 && R.Changes[0].Value == "New" && R.Changes[0].Version == "1"), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await using var App = await CreateAsync(Mock.Of<IIntegrationSettingsService>(), Store.Object);
        var Proposal = await App.Services.GetRequiredService<SiteSettingsActions>().ProposeAsync(new("admin", "Owner", "admin"), "Api", "CoachCheckins:CoachName", "New", default);
        using var Client = App.GetTestClient();
        Sign(Client, "admin", "Owner", true, false);
        (await Client.GetAsync($"/api/admin/settings/proposals/{Proposal.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        Store.Verify(S => S.SaveAsync(It.IsAny<SaveDatabaseSettingsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        (await Client.PostAsJsonAsync($"/api/admin/settings/proposals/{Proposal.Id}/confirm", new { confirm = false })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Client.PostAsJsonAsync($"/api/admin/settings/proposals/{Proposal.Id}/confirm", new { confirm = true, value = "forged", actorId = "victim", role = "Owner" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        Store.Verify(S => S.SaveAsync(It.IsAny<SaveDatabaseSettingsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<WebApplication> CreateAsync(IIntegrationSettingsService Service, IDatabaseSettingsStore? Settings = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["IntegrationSettings:AdministratorIds"] = "admin" });
        builder.Services.AddSingleton(Service);
        builder.Services.AddSingleton<IOtelSettingsStore>(new Mock<IOtelSettingsStore>(MockBehavior.Strict).Object);
        builder.Services.AddSingleton<IOtelExporterFactory>(new Mock<IOtelExporterFactory>(MockBehavior.Strict).Object);
        builder.Services.AddSingleton(new IntegrationHost("Test"));
        builder.Services.AddSingleton<OtelRuntime>();
        builder.Services.AddSingleton<OtelSettingsService>();
        builder.Services.AddSingleton(new DatabaseSettingsStore(new IntegrationDatabase("", "")));
        builder.Services.AddSingleton<IDatabaseSettingsStore>(S => Settings ?? S.GetRequiredService<DatabaseSettingsStore>());
        builder.Services.AddSingleton<SiteSettingsActions>();
        builder.Services.AddSingleton<DatabaseCredentialRuntime>();
        builder.Services.AddSingleton(new AutomationRuntimeStore(new IntegrationDatabase("", "")));
        builder.Services.AddSingleton(new Mock<IWhatboxSettingsStore>(MockBehavior.Strict).Object);
        builder.Services.AddSingleton(new Mock<IWhatboxSshTransport>(MockBehavior.Strict).Object);
        builder.Services.AddSingleton<WhatboxService>();
        var app = builder.Build();
        var group = app.MapGroup("/api").AddEndpointFilter(new InternalApiKeyFilter(Options.Create(new ApiKeyOptions { InternalApiKey = "internal" })));
        group.MapIntegrationSettings(Options.Create(new SecurityOptions { ActorSigningKey = "signing-key" }), builder.Configuration);
        group.MapAutomationRuntimeSettings(Options.Create(new SecurityOptions { ActorSigningKey = "signing-key" }), builder.Configuration);
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
