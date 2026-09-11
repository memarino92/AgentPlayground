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

public sealed class EvidenceDrawerTests : TestContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingTimingAndAudio_AreExplicit_AndRerendersDoNotReload(bool Available)
    {
        using var Handler = new EvidenceHandler(Available);
        Configure(Handler);
        var Id = Guid.NewGuid();
        var Cut = RenderComponent<EvidenceDrawer>(Parameters => Parameters.Add(Value => Value.UploadId, Id).Add(Value => Value.ProfileId, "owner"));
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("No source timestamp"));
        Cut.FindAll("audio").Count.Should().Be(Available ? 1 : 0);
        Cut.Markup.Should().Contain("Synthetic transcript");
        if (!Available) Cut.Markup.Should().Contain("Audio unavailable");
        Cut.SetParametersAndRender(Parameters => Parameters.Add(Value => Value.UploadId, Id));
        Handler.Reads.Should().Be(1);
    }

    [Fact]
    public void DeleteRequiresExplicitAction_ThenRemovesPlayerAndRetainsTranscript()
    {
        using var Handler = new EvidenceHandler(true);
        Configure(Handler);
        var Cut = RenderComponent<EvidenceDrawer>(Parameters => Parameters.Add(Value => Value.UploadId, Guid.NewGuid()).Add(Value => Value.ProfileId, "owner"));
        Cut.WaitForAssertion(() => Cut.FindAll("audio").Should().ContainSingle());
        Cut.FindAll("button").Single(Button => Button.TextContent == "Remove audio…").Click();
        Handler.Deletes.Should().Be(0);
        Cut.FindAll("button").Single(Button => Button.TextContent == "Delete recording").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Audio unavailable"));
        Handler.Deletes.Should().Be(1);
        Cut.FindAll("audio").Should().BeEmpty();
        Cut.Markup.Should().Contain("Synthetic transcript");
    }

    [Fact]
    public void CitationParser_PreservesSource_AndNeverInventsMissingTiming()
    {
        const string Id = "10000000-0000-0000-0000-000000000001";
        var Citations = EvidenceCitation.Parse($"[Call](/evidence/{Id}?profileId=owner&startMs=3200) [Legacy](/evidence/{Id}?profileId=owner)");
        Citations.Should().HaveCount(2);
        Citations[0].StartMs.Should().Be(3200);
        Citations[0].UploadId.Should().Be(Guid.Parse(Id));
        Citations[1].StartMs.Should().BeNull();
        EvidenceCitation.Parse($"/evidence/{Id}?profileId=owner&startMs=-1")[0].StartMs.Should().BeNull();
    }

    private void Configure(EvidenceHandler Handler)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var Authentication = new OwnerAuthentication();
        Services.AddSingleton<AuthenticationStateProvider>(Authentication);
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, Authentication,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test-signing-key" })));
    }

    [Fact]
    public void CitedAnswer_OpensEvidenceBesideTheAnswer()
    {
        using var Handler = new EvidenceHandler(true);
        Configure(Handler);
        const string Id = "10000000-0000-0000-0000-000000000001";
        var Cut = RenderComponent<ChatMessageList>(Parameters => Parameters
            .Add(Value => Value.Messages, [new("assistant", $"Brace before the pull. [Call evidence](/evidence/{Id}?profileId=owner&startMs=4000)")])
            .Add(Value => Value.ContainerId, "test-chat"));
        Cut.FindAll("button").Single(Button => Button.TextContent.StartsWith("Open call evidence", StringComparison.Ordinal)).Click();
        Cut.WaitForAssertion(() => Cut.FindAll("audio").Should().ContainSingle());
        Cut.Find(".message-list").TextContent.Should().Contain("Brace before the pull");
        Cut.FindComponent<EvidenceDrawer>().Instance.StartMs.Should().Be(4000);
        Cut.FindComponent<EvidenceDrawer>().Instance.ProfileId.Should().Be("owner");
    }
    [Theory]
    [InlineData("/evidence/", "?profileId=source&startMs=4000", "source", 4000)]
    [InlineData("https://evidence/", "?startMs=4000", "owner", 4000)]
    [InlineData("https://evidence/", "", "owner", null)]
    public void TimestampLink_TargetsDrawerAction_WithSourceAndTiming(string Prefix, string Query, string ExpectedProfile, int? ExpectedStart)
    {
        using var Handler = new EvidenceHandler(true);
        Configure(Handler);
        const string Id = "80b16b37-10ac-4c41-9352-4faf4d06d2b6";
        var Cut = RenderComponent<ChatMessageList>(Parameters => Parameters
            .Add(Value => Value.Messages, [new("assistant", $"[00:04]({Prefix}{Id}{Query}) [Reference](https://example.com/evidence/{Id})")])
            .Add(Value => Value.ProfileId, "owner")
            .Add(Value => Value.ContainerId, "test-chat"));
        var Link = Cut.Find("a");
        Link.TextContent.Should().Be("00:04");
        Link.GetAttribute("href").Should().StartWith($"/evidence/{Id}?profileId={ExpectedProfile}");
        var Button = Cut.Find("button[data-evidence-url]");
        Button.GetAttribute("data-evidence-url").Should().Be(Link.GetAttribute("href"));
        Cut.FindAll("button[data-evidence-url]").Should().ContainSingle();
        Cut.Find("a[href^='https://example.com']").GetAttribute("href").Should().Be($"https://example.com/evidence/{Id}");
        Button.Click();
        Cut.WaitForAssertion(() => Cut.FindAll("audio").Should().ContainSingle());
        var Drawer = Cut.FindComponent<EvidenceDrawer>().Instance;
        Drawer.UploadId.Should().Be(Guid.Parse(Id));
        Drawer.ProfileId.Should().Be(ExpectedProfile);
        Drawer.StartMs.Should().Be(ExpectedStart);
    }

    private sealed class OwnerAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "Owner"), new Claim("urn:github:login", "owner")], "test"))));
    }
    private sealed class EvidenceHandler(bool Available) : HttpMessageHandler
    {
        public int Reads, Deletes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.Method == HttpMethod.Delete) { Deletes++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)); }
            Reads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new CoachEvidenceResponse(
                new(Guid.NewGuid(), Guid.NewGuid(), "owner", "Completed", "Synthetic transcript", DateTimeOffset.UtcNow, []), Available)) });
        }
    }
}
