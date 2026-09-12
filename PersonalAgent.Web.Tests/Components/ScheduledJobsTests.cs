using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
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

public sealed class ScheduledJobsTests : TestContext
{
    [Fact]
    public void ShowsAttributionLocalTimeAndCancelOutcome()
    {
        using var Handler = new JobHandler();
        Configure(Handler);
        var Cut = RenderComponent<ScheduledJobs>();
        Cut.WaitForAssertion(() => Cut.Find(".job-card").TextContent.Should().Contain("Scheduled by owner").And.Contain("For owner").And.Contain("8:00:00 AM"));
        Cut.Find(".job-card").Click();
        Cut.WaitForAssertion(() => Cut.Find(".job-id").TextContent.Should().Contain(Handler.Id.ToString()));
        Cut.Find(".job-cancel").Click();
        Cut.WaitForAssertion(() => Cut.Find("[role=status]").TextContent.Should().Be("Job cancelled."));
        Handler.Cancelled.Should().BeTrue();
        Cut.FindAll(".job-cancel").Should().BeEmpty();
    }

    [Fact]
    public void ErrorClearsPreviouslyVisibleDetailsAndCanRefresh()
    {
        using var Handler = new JobHandler();
        Configure(Handler);
        var Cut = RenderComponent<ScheduledJobs>();
        Cut.WaitForElement(".job-card").Click();
        Cut.WaitForElement(".job-id");
        Handler.Fail = true;
        Cut.Find(".jobs-header button").Click();
        Cut.WaitForElement("[role=alert]");
        Cut.Markup.Should().NotContain("Synthetic job instruction").And.NotContain("Private error");
        Handler.Fail = false;
        Cut.Find(".jobs-header button").Click();
        Cut.WaitForElement(".job-card");
    }

    [Fact]
    public void DeepLinkLoadsDetailAndCompletedJobCannotBeCancelled()
    {
        using var Handler = new JobHandler { Completed = true };
        Configure(Handler);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo($"/jobs?jobId={Handler.Id}");
        var Cut = RenderComponent<ScheduledJobs>();
        Cut.WaitForAssertion(() => Cut.Find(".job-outcome").TextContent.Should().Be("Synthetic result"));
        Cut.FindAll(".job-cancel").Should().BeEmpty();
        Cut.Find(".job-actions a").GetAttribute("href").Should().Contain("/chat?sessionId=").And.Contain("profileId=owner");
    }

    private void Configure(JobHandler Handler)
    {
        Services.AddLogging();
        var Authentication = new OwnerAuthentication();
        Services.AddSingleton<AuthenticationStateProvider>(Authentication);
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") },
            Authentication, Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "synthetic-signing-key" })));
        JSInterop.SetupModule("./Components/Pages/ScheduledJobs.razor.js").Setup<string>("timeZone").SetResult("America/New_York");
    }

    private sealed class OwnerAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Owner"), new Claim("urn:github:login", "owner")], "Test"))));
    }

    private sealed class JobHandler : HttpMessageHandler
    {
        public Guid Id = Guid.NewGuid();
        public bool Cancelled;
        public bool Completed;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Request.Headers.Contains("X-Agent-Signature").Should().BeTrue();
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Private error") });
            if (Request.Method == HttpMethod.Post) { Cancelled = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            var Date = DateTimeOffset.Parse("2026-09-12T12:00:00Z");
            var Job = new ScheduledJobResponse(Id, "owner", null, "owner", "Synthetic job instruction", Date, Date,
                Completed ? "Completed" : Cancelled ? "Cancelled" : "Scheduled", Completed ? "Synthetic result" : null,
                null, Completed ? Id.ToString() : null, true, Id, 0, Date);
            object Body = Request.RequestUri!.AbsolutePath == "/api/jobs/" ? new[] { Job } : new ScheduledJobDetailResponse(Job, []);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Body, Body.GetType()) });
        }
    }
}
