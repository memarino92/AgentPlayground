using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using PersonalAgent.Integrations.ChatGpt;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class ChatGptOAuthTests
{
    private static ChatGptAuthorization Attempt(string? Client = null) => new("urn:uuid:" + Guid.NewGuid(), "state", "nonce", ChatGptOAuth.RandomValue(), Client, null, null);

    [Fact]
    public void Authorization_UsesDynamicRegistration_Pkce_AndExactLoopback()
    {
        var attempt = Attempt();
        var query = QueryHelpers.ParseQuery(new Uri(ChatGptOAuth.AuthorizeUrl(attempt)).Query);
        query["client_id"].ToString().Should().Be("dynamic_agent_client");
        query["redirect_uri"].ToString().Should().Be("http://127.0.0.1:1455/auth/callback");
        query["ext_agent_host_id"].ToString().Should().Be(attempt.HostId);
        query["scope"].ToString().Should().Contain("chatgpt.tokens.use.direct");
        query["code_challenge"].ToString().Should().NotBe(attempt.Verifier).And.HaveLength(43);
        query["agent_name_hint"].ToString().Should().Be("AgentPlayground");
        QueryHelpers.ParseQuery(new Uri(ChatGptOAuth.AuthorizeUrl(Attempt("oaiapp_existing"))).Query).Should().NotContainKey("agent_name_hint");
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bearer")]
    public async Task Exchange_UsesIssuedClient_AndValidatedIdentity(string TokenType)
    {
        using var handler = new ChatGptTestTransport { TokenType = TokenType };
        using var http = new HttpClient(handler);
        var attempt = Attempt();
        var credential = await new ChatGptOAuth(http).ExchangeAsync(attempt, "state", "code", "oaiapp_test", null, default);
        handler.LastGrant!["client_id"].Should().Be("oaiapp_test");
        handler.LastGrant["code_verifier"].Should().Be(attempt.Verifier);
        handler.LastGrant["redirect_uri"].Should().Be(ChatGptOAuth.RedirectUri);
        handler.LastGrant.Should().NotContainKey("client_secret");
        credential.Subject.Should().Be(handler.Subject);
        ChatGptOAuth.PlanEnabled(credential.Scope).Should().BeTrue();
    }

    [Theory]
    [InlineData("wrong", "code", "oaiapp_test", null)]
    [InlineData("state", "code", "oaiapp_test", "access_denied")]
    [InlineData("state", "code", null, null)]
    [InlineData("state", "code", "dynamic_agent_client", null)]
    public async Task InvalidCallback_NeverExchangesCode(string State, string Code, string? Client, string? Error)
    {
        using var handler = new ChatGptTestTransport();
        using var http = new HttpClient(handler);
        await new ChatGptOAuth(http).Invoking(Value => Value.ExchangeAsync(Attempt(), State, Code, Client, Error, default)).Should().ThrowAsync<ChatGptException>();
        handler.Grants.Should().Be(0);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("expiry")]
    public async Task Identity_MustValidateNonceAudienceIssuerAndExpiry(string Invalid)
    {
        using var handler = new ChatGptTestTransport();
        if (Invalid == "issuer") handler.IdentityIssuer = "https://other.example";
        if (Invalid == "expiry") handler.ExpiredIdentity = true;
        using var http = new HttpClient(handler);
        var jwt = handler.CreateIdentity(Invalid == "nonce" ? "other" : null, Invalid == "audience" ? "oaiapp_other" : null);
        await new ChatGptOAuth(http).Invoking(Value => Value.ValidateIdentityAsync(jwt, "oaiapp_test", "nonce", default)).Should().ThrowAsync<ChatGptException>();
    }

    [Fact]
    public async Task ChangedReturningClient_IsRejectedBeforeTokenExchange()
    {
        using var handler = new ChatGptTestTransport();
        using var http = new HttpClient(handler);
        await new ChatGptOAuth(http).Invoking(Value => Value.ExchangeAsync(Attempt("oaiapp_existing"), "state", "code", "oaiapp_other", null, default)).Should().ThrowAsync<ChatGptException>();
        handler.Grants.Should().Be(0);
    }

    [Fact]
    public async Task IdentityOnlyConsent_IsKeptSeparateFromPlanPermission()
    {
        using var handler = new ChatGptTestTransport { GrantedScope = "openid profile email" };
        using var http = new HttpClient(handler);
        var credential = await new ChatGptOAuth(http).ExchangeAsync(Attempt(), "state", "code", "oaiapp_test", null, default);
        ChatGptOAuth.PlanEnabled(credential.Scope).Should().BeFalse();
    }
}
