using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MudBlazor.Services;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class TranscriptAudioUploadTests : TestContext
{
    [Fact]
    public async Task TranscriptPage_RefreshesCompletionFromLiveEvent()
    {
        using var Handler = new UploadHandler { Status = "Processing" };
        Configure(Handler);
        Services.AddMudServices();
        var Cut = RenderComponent<CoachTranscripts>();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic transcript"));
        Cut.FindComponents<TranscriptAudioUpload>().Should().BeEmpty();
        Handler.Status = "Completed";
        await Task.Run(() => Services.GetRequiredService<CoachCallUpdates>().Notify(new(Handler.Id, "owner", "Completed")));
        Cut.WaitForAssertion(() => Cut.FindComponents<TranscriptAudioUpload>().Should().ContainSingle());
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Processing", false)]
    public void TranscriptPage_OffersUploadForCompletedCall_AndBindsItsSubject(string Status, bool CanUpload)
    {
        using var Handler = new UploadHandler { Status = Status };
        Configure(Handler);
        Services.AddMudServices();
        var Cut = RenderComponent<CoachTranscripts>();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic transcript"));
        Cut.FindComponents<TranscriptAudioUpload>().Count.Should().Be(CanUpload ? 1 : 0);
        if (CanUpload)
        {
            var Upload = Cut.FindComponent<TranscriptAudioUpload>().Instance;
            Upload.ProfileId.Should().Be("owner");
            Upload.UploadId.Should().Be(Handler.Id);
            Upload.MaxUploadBytes.Should().Be(1234);
        }
        Cut.FindAll("button").Single(Button => Button.TextContent.Contains("Read and listen to evidence", StringComparison.OrdinalIgnoreCase)).Click();
        Cut.FindComponent<EvidenceDrawer>().Instance.ProfileId.Should().Be("owner");
    }

    [Theory]
    [InlineData(false, "Upload audio")]
    [InlineData(true, "Replace audio")]
    public async Task FileSelection_AttachesDirectlyToSelectedCall(bool HasAudio, string Label)
    {
        using var Handler = new UploadHandler();
        Configure(Handler);
        var Id = Guid.NewGuid();
        Guid? UploadedId = null;
        var Cut = RenderComponent<TranscriptAudioUpload>(Parameters => Parameters
            .Add(Value => Value.UploadId, Id).Add(Value => Value.ProfileId, "owner").Add(Value => Value.HasAudio, HasAudio)
            .Add(Value => Value.MaxUploadBytes, 1024).Add(Value => Value.OnUploaded, (Guid Value) => UploadedId = Value));
        Cut.Find("label").TextContent.Should().Contain(Label);
        var File = new Mock<IBrowserFile>();
        File.SetupGet(Value => Value.Name).Returns("chosen.m4a");
        File.SetupGet(Value => Value.ContentType).Returns("");
        File.Setup(Value => Value.OpenReadStream(1024, It.IsAny<CancellationToken>())).Returns(new MemoryStream([9, 8, 7]));
        await Cut.InvokeAsync(() => Cut.FindComponent<InputFile>().Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([File.Object])));
        Cut.Find("[role=status]").TextContent.Should().Be("Audio uploaded.");
        Handler.Uploaded.Should().Equal(9, 8, 7);
        Handler.UploadUri.Should().Be($"/api/coach-checkins/{Id}/audio?profileId=owner");
        Handler.UploadMime.Should().Be("audio/mp4");
        Handler.Calls.Should().Be(1);
        UploadedId.Should().Be(Id);
    }

    [Fact]
    public async Task ReadFailure_AllowsRetryWithoutSendingAudio()
    {
        using var Handler = new UploadHandler();
        Configure(Handler);
        var Cut = RenderComponent<TranscriptAudioUpload>();
        var File = new Mock<IBrowserFile>();
        File.Setup(Value => Value.OpenReadStream(It.IsAny<long>(), It.IsAny<CancellationToken>())).Throws(new IOException("Too large"));
        await Cut.InvokeAsync(() => Cut.FindComponent<InputFile>().Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([File.Object])));
        Cut.Find("[role=alert]").TextContent.Should().Contain("Audio could not be uploaded");
        Cut.Find("input[type=file]").HasAttribute("disabled").Should().BeFalse();
        Handler.Calls.Should().Be(0);
    }

    private void Configure(UploadHandler Handler)
    {
        Services.AddSingleton<CoachCallUpdates>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var Auth = new Mock<AuthenticationStateProvider>();
        Auth.Setup(Value => Value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "Owner"), new Claim("urn:github:login", "owner")], "test"))));
        Services.AddSingleton<AuthenticationStateProvider>(Auth.Object);
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, Auth.Object,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test-signing-key" })));
    }

    private sealed class UploadHandler : HttpMessageHandler
    {
        public int Calls;
        public string Status = "Completed";
        public Guid Id = Guid.NewGuid();
        public byte[]? Uploaded;
        public string? UploadUri, UploadMime;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.Method == HttpMethod.Get)
            {
                Request.RequestUri!.Query.Should().Be("?profileId=owner" + (Request.RequestUri.AbsolutePath == "/api/coach-checkins" ? "&limit=100" : ""));
                return new(HttpStatusCode.OK) { Content = Request.RequestUri.AbsolutePath == "/api/coach-checkins"
                    ? JsonContent.Create(new[] { new CoachCheckinAdminItemResponse(Id, Id, "owner", "call.m4a", Status, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, 1, 1, []) })
                    : JsonContent.Create(new CoachEvidenceResponse(new(Id, Id, "owner", Status, "Synthetic transcript", DateTimeOffset.UtcNow,
                        [new(0, "coach", 0, 1000, "Synthetic transcript", 1)]), false, 1234)) };
            }
            Request.Method.Should().Be(HttpMethod.Put);
            Uploaded = await Request.Content!.ReadAsByteArrayAsync(CancellationToken);
            UploadUri = Request.RequestUri!.PathAndQuery;
            UploadMime = Request.Content.Headers.ContentType!.MediaType;
            Calls++;
            return new(HttpStatusCode.NoContent);
        }
    }
}
