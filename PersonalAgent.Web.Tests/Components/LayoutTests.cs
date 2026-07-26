using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PersonalAgent.Web.Components;
using PersonalAgent.Web.Extensions;
using System.Security.Claims;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public class LayoutTests : TestContext
{
    public LayoutTests()
    {
        Services.AddMudServices();
        Services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(ServiceCollectionAuthenticationExtensions.OwnerPolicy, policy => policy.RequireRole("Owner"));
            options.AddPolicy(ServiceCollectionAuthenticationExtensions.CoachTranscriptPolicy, policy => policy.RequireRole("Owner", "Coach"));
        });
        JSInterop.Setup<int>("mudpopoverHelper.countProviders").SetResult(1);
        JSInterop.SetupVoid("watchDarkThemeMedia", _ => true);
    }

    [Fact]
    public void Layout_ShowsBody_WhenUserIsAnonymous()
    {
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

        cut.Markup.Should().Contain("body");
    }

    [Fact]
    public void Layout_ShowsBody_WhenUserIsAuthorized()
    {
        Services.AddCascadingAuthenticationState();
        var authContext = this.AddTestAuthorization();
        authContext.SetAuthorized("michael");
        authContext.SetClaims(new Claim(ClaimTypes.Name, "Michael"));
        authContext.SetRoles("Owner");

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
    }
}
