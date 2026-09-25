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

public sealed class AutomationsTests : TestContext
{
    [Fact]
    public void DashboardShowsVersionsScheduleRunOutputsAndPause()
    {
        using var Handler = new AutomationHandler();
        Configure(Handler);
        var Cut = RenderComponent<Automations>();
        Cut.WaitForElement(".automation-item").TextContent.Should().Contain("v2").And.NotContain("@Item");
        Cut.Find(".automation-item").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Versions and source").And.Contain("Upcoming runs").And.Contain("2026-09-25"));
        Cut.FindAll("button").Single(B => B.TextContent == "Inspect run").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic saved content").And.Contain("trace-synthetic")
            .And.Contain("C# build and execution").And.Contain("sha256:compiler"));
        Cut.FindAll("button").Single(B => B.TextContent == "Pause future runs").Click();
        Cut.WaitForAssertion(() => Handler.Paused.Should().BeTrue());
        Cut.WaitForAssertion(() => Cut.FindAll("button").Should().Contain(B => B.TextContent == "Resume"));
        Cut.Markup.Should().NotContain("<script>");
    }

    [Fact]
    public void AccessFailureClearsSourceAndResults()
    {
        using var Handler = new AutomationHandler();
        Configure(Handler);
        var Cut = RenderComponent<Automations>();
        Cut.WaitForElement(".automation-item").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Versions and source"));
        Handler.Fail = true;
        Cut.Find(".automation-header button").Click();
        Cut.WaitForElement("[role=alert]");
        Cut.Markup.Should().NotContain("Versions and source").And.NotContain("Private server detail");
    }

    private void Configure(AutomationHandler Handler)
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
    private sealed class AutomationHandler : HttpMessageHandler
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly Guid RunId = Guid.NewGuid();
        public bool Fail;
        public bool Paused;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Request.Headers.Contains("X-Agent-Signature").Should().BeTrue();
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Private server detail") });
            if (Request.Method == HttpMethod.Post) { Paused = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            var Date = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var Definition = new AutomationSummary(Id, "Daily report", Paused ? "Paused" : "Active", 2, Date, Date.AddDays(1), TimeSpan.FromDays(1), "owner", "owner");
            var Run = new AutomationRunResponse(RunId, 1, "Completed", Date, Date, Date, null, "trace-synthetic");
            object Body = Request.RequestUri!.AbsolutePath.Contains("/runs/")
                ? new AutomationRunDetail(Run, [new(0, "program", "csharp", "Completed", "Synthetic saved content", null, Date,
                    "{\"ImageId\":\"sha256:compiler\",\"StandardError\":\"<script>untrusted</script>\"}")], [])
                : Request.RequestUri.AbsolutePath == "/api/automations/" ? new[] { Definition }
                : new AutomationDetail(Definition, [new(2, "{\"steps\":[]}", "hash", Date), new(1, "{\"steps\":[]}", "old-hash", Date)], [Run], Paused ? [] : [Date.AddDays(1)]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Body, Body.GetType()) });
        }
    }
}
