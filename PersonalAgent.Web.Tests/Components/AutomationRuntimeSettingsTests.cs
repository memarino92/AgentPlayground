using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class AutomationRuntimeSettingsTests : TestContext
{
    [Fact]
    public void FreshSetupSavesAndClearsCredentialFromForm()
    {
        using var Handler = new Handler();
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, new Anonymous(), Options.Create(new PersonalAgentApiOptions())));
        var Page = RenderComponent<AutomationRuntimeSettingsPage>();
        Page.WaitForAssertion(() => Page.Markup.Should().Contain("token missing"));
        Page.Find("select").Change("replace");
        Page.Find("input[type=password]").Change("private-token");
        Page.Find("form").Submit();
        Page.WaitForAssertion(() => Page.Markup.Should().Contain("token configured"));
        Handler.Saved!.ExpectedRevision.Should().Be(0);
        Handler.Saved.Token.Should().Be("private-token");
        Page.Markup.Should().NotContain("private-token");
        Page.FindAll("input[type=password]").Should().BeEmpty();
    }
    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public SaveAutomationRuntime? Saved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            if (Request.Method == HttpMethod.Put) Saved = await Request.Content!.ReadFromJsonAsync<SaveAutomationRuntime>(Token);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new AutomationRuntimeView(Saved is null ? 0 : 1, Saved?.Settings ?? new(), Saved is not null)) };
        }
    }
}
