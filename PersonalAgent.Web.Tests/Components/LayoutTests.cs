using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Web.Components;
using System.Security.Claims;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public class LayoutTests : TestContext
{
    [Fact]
    public void Layout_ShowsLoginPrompt_WhenUserIsAnonymous()
    {
        Services.AddAuthorizationCore();
        Services.AddCascadingAuthenticationState();
        var authContext = this.AddTestAuthorization();
        authContext.SetNotAuthorized();

        var cut = RenderComponent<Layout>(parameters => parameters
            .Add(layout => layout.Body, (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "p");
                builder.AddContent(1, "body");
                builder.CloseElement();
            })));

        cut.Markup.Should().Contain("Please log in to continue");
        cut.Markup.Should().Contain("Send test event");
        cut.Markup.Should().Contain("Sessions");
    }

    [Fact]
    public void Layout_ShowsBody_WhenUserIsAuthorized()
    {
        Services.AddAuthorizationCore();
        Services.AddCascadingAuthenticationState();
        var authContext = this.AddTestAuthorization();
        authContext.SetAuthorized("michael");
        authContext.SetClaims(new Claim(ClaimTypes.Name, "Michael"));

        var cut = RenderComponent<Layout>(parameters => parameters
            .Add(layout => layout.Body, (RenderFragment)(builder =>
            {
                builder.OpenElement(0, "p");
                builder.AddAttribute(1, "id", "authorized-body");
                builder.AddContent(2, "body");
                builder.CloseElement();
            })));

        cut.Find("#authorized-body").TextContent.Should().Be("body");
        cut.Markup.Should().Contain("michael");
        cut.Markup.Should().Contain("Log out");
        cut.Markup.Should().Contain("Send test event");
        cut.Markup.Should().Contain("Sessions");
    }
}
