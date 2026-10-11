using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PersonalAgent.Integrations.ChatGpt;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class ChatGptSettingsTests : TestContext
{
    [Fact]
    public void MissingConnection_CanStartPairing_WithoutExposingTokens()
    {
        using var handler = Register();
        var cut = RenderComponent<ChatGptSettings>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No ChatGPT accounts connected"));
        cut.Find(".chatgpt-continue").Click();
        cut.WaitForAssertion(() => cut.Find(".chatgpt-pairing").TextContent.Should().Contain("pairing-code").And.Contain("scripts/ChatGptConnect"));
        cut.Markup.Should().NotContain("AccessToken").And.NotContain("RefreshToken");
        handler.PairingStarted.Should().BeTrue();
    }

    [Fact]
    public void Welcome_CanBeDismissed_AndUnconfirmedRevocationIsExplained()
    {
        using var handler = Register();
        handler.Connections = [new(Guid.NewGuid(), "account · workspace", true, true, 1, true)];
        var cut = RenderComponent<ChatGptSettings>();
        cut.WaitForAssertion(() => cut.Find("dialog").TextContent.Should().Contain("You’re using your ChatGPT plan"));
        cut.Find("dialog button").Click();
        cut.WaitForAssertion(() => cut.FindAll("dialog").Should().BeEmpty());
        cut.Find(".chatgpt-account button:last-child").Click();
        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.Should().Contain("Remote revocation was not confirmed"));
    }

    [Fact]
    public void OlderApi_ShowsVersionMismatch_AndAllowsRetry()
    {
        using var handler = Register();
        handler.Unavailable = true;
        var cut = RenderComponent<ChatGptSettings>();
        cut.WaitForAssertion(() => cut.Find("[role=alert]").TextContent.Should().Contain("Deploy the current API version"));
        cut.FindAll("button").Should().Contain(Value => Value.TextContent == "Refresh connections");
    }

    private Handler Register()
    {
        var handler = new Handler();
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(handler) { BaseAddress = new("https://instance.test") },
            new Authentication(), Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test-signing-key" })));
        return handler;
    }

    private sealed class Authentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "owner"), new(ClaimTypes.Role, "Owner")], "test"))));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<ChatGptConnection> Connections = [];
        public bool PairingStarted;
        public bool Unavailable;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            if (Unavailable) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            if (Request.Method == HttpMethod.Get) return Task.FromResult(Ok(Connections));
            if (Request.RequestUri!.AbsolutePath.EndsWith("/pairings", StringComparison.Ordinal))
            {
                PairingStarted = true;
                return Task.FromResult(Ok(new ChatGptPairing("pairing-code", DateTimeOffset.UtcNow.AddMinutes(10))));
            }
            if (Request.RequestUri.AbsolutePath.EndsWith("/welcome", StringComparison.Ordinal))
            {
                Connections = [.. Connections.Select(Value => Value with { WelcomeRequired = false })];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            Connections = [.. Connections.Select(Value => Value with { Connected = false })];
            return Task.FromResult(Ok(new ChatGptDisconnect(false)));
        }
        private static HttpResponseMessage Ok(object Value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(Value) };
    }
}
