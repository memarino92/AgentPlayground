using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AgentPlayground.Contracts.Events;
using Bunit;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MudBlazor.Services;
using PersonalAgent.Web.Components.CoachAdmin;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Consumers;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class CoachCallLiveUpdatesTests : TestContext
{
    [Fact]
    public async Task Consumer_FansOutOnlyMatchingInvalidations_AndUnsubscribes()
    {
        var Updates = new CoachCallUpdates();
        var Id = Guid.NewGuid();
        using var First = Updates.Subscribe("owner", Id);
        using var Second = Updates.Subscribe("owner");
        using var Other = Updates.Subscribe("other");
        using var OtherUpload = Updates.Subscribe("owner", Guid.NewGuid());
        using var Disposed = Updates.Subscribe("owner");
        Disposed.Dispose();
        var Context = Mock.Of<ConsumeContext<CoachCallStatusChangedEvent>>(Value => Value.Message == new CoachCallStatusChangedEvent(Id, "owner", "Completed"));

        for (var Index = 0; Index < 100; Index++) await new CoachCallStatusChangedConsumer(Updates).Consume(Context);

        First.Reader.TryRead(out _).Should().BeTrue();
        First.Reader.TryRead(out _).Should().BeFalse("notifications are coalesced");
        Second.Reader.TryRead(out _).Should().BeTrue();
        Other.Reader.TryRead(out _).Should().BeFalse();
        OtherUpload.Reader.TryRead(out _).Should().BeFalse();
        Disposed.Reader.Completion.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task LiveComponent_RefreshesFromBackground_AndStopsOnDisposal()
    {
        var Updates = new CoachCallUpdates();
        Services.AddSingleton(Updates);
        var Calls = 0;
        var Cut = RenderComponent<CoachCallLiveRefresh>(Parameters => Parameters
            .Add(Value => Value.ProfileId, "owner")
            .Add(Value => Value.OnRefresh, () => Calls++));
        await Task.Run(() => Updates.Notify(new(Guid.NewGuid(), "owner", "Processing")));
        Cut.WaitForAssertion(() => Calls.Should().Be(1));
        await Cut.InvokeAsync(async () => await Cut.Instance.DisposeAsync());
        Updates.Notify(new(Guid.NewGuid(), "owner", "Completed"));
        Calls.Should().Be(1);
    }

    [Fact]
    public async Task UploadPage_ContinuesLiveUpdatesAfterSpeakerReview()
    {
        using var Handler = new UploadStatusHandler();
        var Updates = Configure(Handler);
        var Cut = RenderComponent<CoachCheckins>();
        var File = new Mock<IBrowserFile>();
        File.SetupGet(Value => Value.Name).Returns("synthetic.m4a");
        File.SetupGet(Value => Value.ContentType).Returns("audio/mp4");
        File.SetupGet(Value => Value.Size).Returns(3);
        File.Setup(Value => Value.OpenReadStream(3, It.IsAny<CancellationToken>())).Returns(new MemoryStream([1, 2, 3]));
        await Cut.InvokeAsync(() => Cut.FindComponent<InputFile>().Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([File.Object])));
        Cut.FindAll("button").Single(Button => Button.TextContent.Contains("Upload call")).Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Speaker override is required"));
        Handler.Status = "Processing";
        Updates.Notify(new(Handler.Id, "owner", "Processing"));
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Current status: Processing"));
        Handler.Status = "Completed";
        Updates.Notify(new(Handler.Id, "owner", "Completed"));
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic summary"));
    }

    [Fact]
    public async Task Admin_LiveRefreshPreservesDrafts_AndFirstApplyRefreshesSavedRoles()
    {
        using var Handler = new AdminHandler();
        var Updates = Configure(Handler);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var Cut = RenderComponent<CoachCheckinsAdmin>();
        Cut.WaitForAssertion(() => Cut.FindComponent<UploadDetailPanel>().Instance.SelectedItem.Should().NotBeNull());
        var Detail = Cut.FindComponent<UploadDetailPanel>();
        await Cut.InvokeAsync(() => Detail.Instance.OnRoleChanged.InvokeAsync(new(Handler.Id, 0, "coach")));
        await Cut.InvokeAsync(() => Detail.Instance.OnRoleChanged.InvokeAsync(new(Handler.Id, 1, "athlete")));

        Handler.ChunkCount = 3;
        await Task.Run(() => Updates.Notify(new(Handler.Id, "owner", "AwaitingSpeakerOverride")));
        Cut.WaitForAssertion(() => Detail.Instance.SelectedItem!.ChunkCount.Should().Be(3));
        Detail.Instance.GetOverrideSelection(new(0, "unknown", 1, [])).Should().Be("coach");
        Detail.Instance.GetOverrideSelection(new(1, "unknown", 1, [])).Should().Be("athlete");

        await Cut.InvokeAsync(() => Detail.Instance.OnApplyOverride.InvokeAsync(Detail.Instance.SelectedItem!));
        Handler.Applied.Should().Equal(new SpeakerOverrideItem(0, "coach"), new SpeakerOverrideItem(1, "athlete"));
        Handler.ApplyCount.Should().Be(1);
        Cut.WaitForAssertion(() => Detail.Instance.Transcript!.Utterances.Select(Value => Value.SpeakerRole).Should().Equal("coach", "athlete"));
        Detail.Instance.SelectedItem!.Status.Should().Be("Processing");

        Handler.Status = "Completed";
        Updates.Notify(new(Handler.Id, "owner", "Transcribing")); // Delayed event must not regress displayed state.
        Cut.WaitForAssertion(() => Detail.Instance.SelectedItem!.Status.Should().Be("Completed"));
        Cut.FindAll("button").Single(Button => Button.TextContent.Contains("Apply speaker override")).HasAttribute("disabled").Should().BeTrue();
    }

    private CoachCallUpdates Configure(HttpMessageHandler Handler)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        var Updates = new CoachCallUpdates();
        Services.AddSingleton(Updates);
        var Auth = new Mock<AuthenticationStateProvider>();
        Auth.Setup(Value => Value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "Owner"), new Claim("urn:github:login", "owner")], "test"))));
        Services.AddSingleton<AuthenticationStateProvider>(Auth.Object);
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, Auth.Object,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test-signing-key" })));
        return Updates;
    }

    private sealed class AdminHandler : HttpMessageHandler
    {
        public Guid Id = Guid.NewGuid();
        public string Status = "AwaitingSpeakerOverride";
        public int ChunkCount, ApplyCount;
        public List<SpeakerOverrideItem> Applied = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.Method == HttpMethod.Post)
            {
                var Payload = await Request.Content!.ReadFromJsonAsync<OverridePayload>(CancellationToken);
                Applied = Payload!.Overrides;
                ApplyCount++;
                Status = "Processing";
                return new(HttpStatusCode.NoContent);
            }
            string Role(int Label) => Applied.FirstOrDefault(Value => Value.SpeakerLabel == Label)?.Role ?? "unknown";
            return new(HttpStatusCode.OK)
            {
                Content = Request.RequestUri!.AbsolutePath == "/api/coach-checkins/admin"
                    ? JsonContent.Create(new[] { new CoachCheckinAdminItemResponse(Id, Id, "owner", "synthetic.m4a", Status, null,
                        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, 2, ChunkCount,
                        [new(0, Role(0), 1, []), new(1, Role(1), 1, [])]) })
                    : JsonContent.Create(new CoachCheckinTranscriptResponse(Id, Id, "owner", Status, "synthetic", DateTimeOffset.UtcNow,
                        [new(0, Role(0), 0, 100, "one", 1), new(1, Role(1), 101, 200, "two", 1)]))
            };
        }

        private record OverridePayload(List<SpeakerOverrideItem> Overrides);
    }

    private sealed class UploadStatusHandler : HttpMessageHandler
    {
        public Guid Id = Guid.NewGuid();
        public string Status = "AwaitingSpeakerOverride";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Request.Method == HttpMethod.Post
                ? JsonContent.Create(new CoachCheckinUploadResponse(Id, Id, "Uploaded", DateTimeOffset.UtcNow, false))
                : Request.RequestUri!.AbsolutePath.EndsWith("/summary", StringComparison.Ordinal)
                    ? JsonContent.Create(new CoachCheckinSummaryResponse(Id, Id, "Synthetic summary", "{}", DateTimeOffset.UtcNow))
                    : JsonContent.Create(new CoachCheckinStatusResponse(Id, Id, "owner", Status, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
        });
    }
}
