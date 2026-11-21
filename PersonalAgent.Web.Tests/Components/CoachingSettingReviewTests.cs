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

public sealed class CoachingSettingReviewTests : TestContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Review_DoesNotSaveWithoutClick_AndShowsOnlyActualSaveOutcome(bool Fail)
    {
        using var Handler = new ReviewHandler { Fail = Fail };
        var Http = new HttpClient(Handler) { BaseAddress = new("http://localhost") };
        Services.AddSingleton(new PersonalAgentClient(Http, new AnonymousAuthentication(), Options.Create(new PersonalAgentApiOptions())));
        var Cut = RenderComponent<CoachingSettingReview>(P => P.Add(C => C.Id, Handler.Id));
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Old coach").And.Contain("New coach").And.Contain("API restart required"));
        Handler.Confirmations.Should().Be(0);
        Cut.Find("button").Click();
        Cut.WaitForAssertion(() => Handler.Confirmations.Should().Be(1));
        if (Fail)
        {
            Cut.Find("[role=alert]").TextContent.Should().Contain("could not be confirmed");
            Cut.FindAll("[role=status]").Should().BeEmpty();
        }
        else Cut.Find("[role=status]").TextContent.Should().Contain("Saved to the database");
    }

    private sealed class AnonymousAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }
    private sealed class ReviewHandler : HttpMessageHandler
    {
        public Guid Id = Guid.NewGuid();
        public bool Fail;
        public int Confirmations;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.Method == HttpMethod.Get) return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new CoachingSettingProposal(Id, "Api", "CoachCheckins:CoachName", "Old coach", "New coach", "")) };
            Request.RequestUri!.AbsolutePath.Should().Be($"/api/admin/settings/proposals/{Id}/confirm");
            (await Request.Content!.ReadFromJsonAsync<ConfirmCoachingSetting>(CancellationToken))!.Confirm.Should().BeTrue();
            Confirmations++;
            return new(Fail ? HttpStatusCode.Conflict : HttpStatusCode.NoContent);
        }
    }
}
