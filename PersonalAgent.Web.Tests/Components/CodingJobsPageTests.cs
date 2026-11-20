using System.Net;
using System.Net.Http.Json;
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

        Page.WaitForAssertion(() => Page.Find(".job-metadata").TextContent.Should().Contain("Branch: feature/coding-dashboard")
            .And.Contain("Railway sandbox ID: sandbox-123"));
    }

    [Fact]
    public void DashboardOmitsSandboxRowWhenSandboxIdIsMissing()
    {
        using var Handler = new Handler { SandboxId = null, Status = "Queued" };
        Configure(Handler);

        var Page = RenderComponent<CodingJobsPage>();

        Page.WaitForAssertion(() => Page.Find(".job-metadata").TextContent.Should().Contain("Branch: feature/coding-dashboard"));
        Page.Markup.Should().NotContain("Railway sandbox ID:");
    }

    [Fact]
    public async Task OutstandingQuestionCanBeAnsweredFromTheDashboard()
    {
        var Question = new CodingQuestion(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Which database?", null, DateTimeOffset.UtcNow.AddMinutes(10));
        using var Handler = new Handler { Question = Question };
        Configure(Handler);
        var Page = RenderComponent<CodingJobsPage>();
        Page.WaitForAssertion(() => Page.Find("#outstanding-questions").TextContent.Should().Contain(Question.Text));
        Page.Find("#outstanding-questions a").GetAttribute("href").Should().Be($"/chat?sessionId={Question.SessionId:D}");
        await Page.Find("textarea").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "PostgreSQL" });
        await Page.Find("#outstanding-questions button").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Page.WaitForAssertion(() => Page.Find("#outstanding-questions").TextContent.Should().Contain("No coding jobs are waiting"));
        Handler.Answer.Should().Be(new AnswerCodingQuestion("PostgreSQL"));
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
        public CodingQuestion? Question;
        public AnswerCodingQuestion? Answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Request.Headers.Contains("X-Agent-Signature").Should().BeTrue();
            if (Request.RequestUri!.AbsolutePath == "/api/coding-jobs/questions")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Question is { } Waiting ? new[] { Waiting } : Array.Empty<CodingQuestion>()) };
            if (Question is { } Open && Request.RequestUri.AbsolutePath == $"/api/coding-jobs/questions/{Open.Id:D}/answer")
            {
                Answer = await Request.Content!.ReadFromJsonAsync<AnswerCodingQuestion>(Token);
                Question = null;
                return new(HttpStatusCode.OK);
            }

            var Date = DateTimeOffset.Parse("2026-09-27T12:00:00Z");
            var Job = new CodingJobView(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Implement dashboard updates",
                "memarino92/AgentPlayground", "base-sha", "feature/coding-dashboard", Status, Date, Date.AddHours(1), SandboxId,
                "Active", 2, null, null, null);

            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Job }) };
        }
    }
}
