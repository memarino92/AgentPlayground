using System.Net;
using System.Security.Claims;

using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class CodingJobsPageTests : TestContext
{
    [Fact]
    public void DashboardShowsBranchAndSandboxIdWhenAvailable()
    {
        using var Handler = new Handler();
        Configure(Handler);

        var Page = RenderComponent<CodingJobsPage>();

        Page.WaitForAssertion(() => Page.Markup.Should().Contain("Branch: feature/coding-dashboard")
            .And.Contain("Railway sandbox ID: sandbox-123"));
    }

    [Fact]
    public void DashboardOmitsSandboxRowWhenSandboxIdIsMissing()
    {
        using var Handler = new Handler { SandboxId = null, Status = "Queued" };
        Configure(Handler);

        var Page = RenderComponent<CodingJobsPage>();

        Page.WaitForAssertion(() => Page.Markup.Should().Contain("Branch: feature/coding-dashboard"));
        Page.Markup.Should().NotContain("Railway sandbox ID:");
    }

    private void Configure(Handler Handler)
    {
        Services.AddLogging();
        Services.AddCascadingAuthenticationState();
        var Auth = new OwnerAuthentication();
        Services.AddSingleton<AuthenticationStateProvider>(Auth);
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, Auth,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "synthetic-key" })));
    }

    private sealed class OwnerAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("urn:github:login", "owner"), new Claim(ClaimTypes.Role, "Owner")], "test"))));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? SandboxId = "sandbox-123";
        public string Status = "Running";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Request.Headers.Contains("X-Agent-Signature").Should().BeTrue();

            var Date = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
            var Job = new CodingJobView(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Implement dashboard updates",
                "memarino92/AgentPlayground", "base-sha", "feature/coding-dashboard", Status, Date, Date.AddHours(1), SandboxId,
                "Active", 2, null, null, null);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Job }) });
        }
    }
}
