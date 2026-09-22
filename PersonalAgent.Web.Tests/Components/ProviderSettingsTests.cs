using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PersonalAgent.Integrations;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class ProviderSettingsTests : TestContext
{
    [Fact]
    public void MissingOpenRouterKey_CanBeCreatedWithoutExposingItAfterSave()
    {
        using var handler = new ProviderSettingsHandler();
        var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        Services.AddSingleton(new PersonalAgentClient(http, new AnonymousAuthentication(), Options.Create(new PersonalAgentApiOptions())));
        var cut = RenderComponent<ProviderSettings>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Not configured"));

        cut.Find("input[type=password]").Change("private-openrouter-key");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.Should().Contain("OpenRouter key saved"));
        handler.Saved.Should().Be(new SaveProviderCredentialRequest(null, "private-openrouter-key"));
        handler.Reloaded.Should().BeTrue();
        cut.Markup.Should().Contain("Configured").And.NotContain("private-openrouter-key");
        cut.Find("input[type=password]").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void InvalidKey_IsRejectedBeforeApiCall()
    {
        using var handler = new ProviderSettingsHandler();
        var http = new HttpClient(handler) { BaseAddress = new("http://localhost") };
        Services.AddSingleton(new PersonalAgentClient(http, new AnonymousAuthentication(), Options.Create(new PersonalAgentApiOptions())));
        var cut = RenderComponent<ProviderSettings>();
        cut.WaitForAssertion(() => cut.Find("input[type=password]").Should().NotBeNull());

        cut.Find("input[type=password]").Change("contains spaces");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find(".validation-message").TextContent.Should().Contain("without spaces"));
        handler.Saved.Should().BeNull();
    }

    private sealed class AnonymousAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }

    private sealed class ProviderSettingsHandler : HttpMessageHandler
    {
        public SaveProviderCredentialRequest? Saved;
        public bool Reloaded;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Saved is null ? Array.Empty<DatabaseSetting>() :
                        [new DatabaseSetting("Api", "OpenRouter:ApiKey", "2", null, true, true)])
                };
            if (Request.Method == HttpMethod.Put)
            {
                Saved = await Request.Content!.ReadFromJsonAsync<SaveProviderCredentialRequest>(CancellationToken);
                return new(HttpStatusCode.NoContent);
            }
            Reloaded = true;
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new DatabaseCredentialStatus("Credentials reloaded", DateTimeOffset.UtcNow, []))
            };
        }
    }
}
