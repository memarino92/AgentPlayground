using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;

using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Tests.Components;

public sealed class CodingSettingsTests : TestContext
{
    [Fact]
    public void FreshSetupSendsExplicitSecretActionsAndClearsInputs()
    {
        using var Handler = new Handler();
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, new Anonymous(), Options.Create(new PersonalAgentApiOptions())));
        var Page = RenderComponent<CodingSettingsPage>();
        Page.WaitForAssertion(() => Page.Markup.Should().Contain("OpenRouter key missing"));
        Page.Find("#coding-router-action").Change("replace");
        Page.Find("#coding-router").Change("private-router-key");
        Page.Find("#coding-github-action").Change("replace");
        Page.Find("#coding-github").Change("private-pem");
        Page.Find("#coding-ruleset").Change("123");
        Page.Find("#coding-ruleset-updated").Change("2026-09-27T21:00:00.841-04:00");
        Page.Find("form").Submit();
        Page.WaitForAssertion(() => Page.Markup.Should().Contain("OpenRouter key configured"));
        Handler.Saved!.ExpectedRevision.Should().Be(0);
        Handler.Saved.OpenRouterKeyAction.Should().Be("replace");
        Handler.Saved.GitHubPrivateKey.Should().Be("private-pem");
        Handler.Saved.Settings.VerifiedRulesetId.Should().Be(123);
        Handler.Saved.Settings.VerifiedRulesetUpdatedAt.Should().Be("2026-09-27T21:00:00.841-04:00");
        Page.Find("#coding-ruleset-updated").GetAttribute("value").Should().Be("2026-09-27T21:00:00.841-04:00");
        Page.Markup.Should().NotContain("private-router-key").And.NotContain("private-pem");
        Page.FindAll("#coding-router").Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "Settings changed")]
    [InlineData(HttpStatusCode.Forbidden, "administrator access")]
    [InlineData(HttpStatusCode.NotFound, "matching API")]
    public void SetupFailuresRemainActionable(HttpStatusCode Status, string Message)
    {
        using var Handler = new Handler { Status = Status };
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, new Anonymous(), Options.Create(new PersonalAgentApiOptions())));
        var Page = RenderComponent<CodingSettingsPage>();
        Page.WaitForAssertion(() => Page.Markup.Should().Contain(Message));
    }
    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public SaveCodingSettings? Saved;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            if (Request.RequestUri!.AbsolutePath.EndsWith("/models"))
                return new(Status) { Content = JsonContent.Create(new[] { new CodingModelOption(CodingJobs.Model, "Mini · OpenRouter"),
                    new CodingModelOption(SubscriptionModel, "Astra · ChatGPT · owner account") }) };
            if (Request.Method == HttpMethod.Put) Saved = await Request.Content!.ReadFromJsonAsync<SaveCodingSettings>(Token);
            return new(Status) { Content = JsonContent.Create(new CodingSettingsView(Saved is null ? 0 : 1, Saved?.Settings ?? new(), Saved is not null, Saved is not null)) };
        }
    }
    private const string SubscriptionModel = "chatgpt:cb8c5a00-985c-4504-9781-deb1a836a500:gpt-6-astra";

    [Fact]
    public void SubscriptionSelectionSavesAccountAndLargeLimitWithoutRouterCredentials()
    {
        using var Handler = new Handler();
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, new Anonymous(), Options.Create(new PersonalAgentApiOptions())));
        var Page = RenderComponent<CodingSettingsPage>();
        Page.WaitForAssertion(() => Page.Find("#coding-model").TextContent.Should().Contain("Astra · ChatGPT"));
        Page.Find("#coding-model").Change(SubscriptionModel);
        Page.Find("#coding-requests").GetAttribute("value").Should().Be("250");
        Page.FindAll("#coding-budget").Should().BeEmpty();
        Page.Markup.Should().Contain("No automatic fallback").And.Contain("chatgpt.com/settings/usage");
        Page.Find("#coding-requests").Change("500");
        Page.Find("form").Submit();
        Page.WaitForAssertion(() => Handler.Saved.Should().NotBeNull());
        Handler.Saved!.Settings.ModelId.Should().Be(SubscriptionModel);
        Handler.Saved.Settings.MaxModelRequests.Should().Be(500);
        Handler.Saved.OpenRouterKeyAction.Should().Be("keep");
        Page.Find("#coding-model").Change(CodingJobs.Model);
        Page.Find("#coding-requests").GetAttribute("value").Should().Be("12");
        Page.FindAll("#coding-budget").Should().ContainSingle();
    }
}
