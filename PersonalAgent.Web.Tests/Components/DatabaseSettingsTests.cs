using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AgentPlayground.Integrations;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class DatabaseSettingsTests : TestContext
{
    [Fact]
    public void SaveAcrossFilters_KeepsHiddenSecrets_AndClearsReplacementAfterSave()
    {
        using var handler = new SettingsHandler();
        Configure(handler);
        var cut = RenderComponent<DatabaseSettings>();
        cut.WaitForAssertion(() => cut.FindAll("textarea").Should().ContainSingle());
        cut.Markup.Should().Contain("API service").And.Contain("Restart required").And.Contain("Custom:Unknown");
        cut.FindAll("input[type=password]").Should().BeEmpty();
        cut.Find("textarea").Change("new-value");
        cut.Find("input[type=search]").Input("Token");
        cut.FindAll("textarea").Should().BeEmpty();
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => handler.Saved.Should().NotBeNull());
        handler.Saved!.Changes.Should().ContainSingle().Which.Key.Should().Be("Custom:Unknown");
        cut.Find("select[aria-label]").Change("replace");
        cut.Find("input[type=password]").Change("private-replacement");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => handler.Saved!.Changes.Single().Value.Should().Be("private-replacement"));
        cut.FindAll("input[type=password]").Should().BeEmpty();
        cut.Markup.Should().NotContain("private-replacement");
    }

    [Fact]
    public void SecretClear_IsExplicit_AndConflictDoesNotClaimSaved()
    {
        using var handler = new SettingsHandler { Status = HttpStatusCode.Conflict };
        Configure(handler);
        var cut = RenderComponent<DatabaseSettings>();
        cut.WaitForAssertion(() => cut.FindAll("select[aria-label]").Should().ContainSingle());
        cut.Find("select[aria-label]").Change("clear");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("Nothing in this batch was saved"));
        handler.Saved!.Changes.Single().Value.Should().BeEmpty();
        cut.FindAll("[role=status]").Should().BeEmpty();
    }

    private void Configure(SettingsHandler Handler)
    {
        var http = new HttpClient(Handler) { BaseAddress = new("http://localhost") };
        Services.AddSingleton(new PersonalAgentClient(http, new AnonymousAuthentication(), Options.Create(new PersonalAgentApiOptions())));
    }
    private sealed class AnonymousAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }
    private sealed class SettingsHandler : HttpMessageHandler
    {
        public SaveDatabaseSettingsRequest? Saved;
        public HttpStatusCode Status = HttpStatusCode.NoContent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] {
                    new DatabaseSetting("Api", "Custom:Token", "1", null, true, true),
                    new DatabaseSetting("Worker", "Custom:Unknown", "2", "original", false, false) }) };
            Saved = await Request.Content!.ReadFromJsonAsync<SaveDatabaseSettingsRequest>(CancellationToken);
            return new(Status);
        }
    }
}
