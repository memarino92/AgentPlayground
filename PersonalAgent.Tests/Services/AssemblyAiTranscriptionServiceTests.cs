using System.Net;
using System.Text;
using System.Text.Json;
using AgentPlayground.Contracts.Messaging.Responses;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class AssemblyAiTranscriptionServiceTests
{
    [Fact]
    public async Task Submission_UploadsAudio_AndUsesApiProviderPolicy()
    {
        var Requests = new List<(string Path, string Body)>();
        using var Handler = new StubHandler(async Request =>
        {
            Requests.Add((Request.RequestUri!.AbsolutePath, await Request.Content!.ReadAsStringAsync()));
            return Json(Requests.Count == 1 ? "{\"upload_url\":\"https://upload.invalid/audio\"}" : "{\"id\":\"job-123\"}");
        });
        var Service = Create(Handler);
        (await Service.SubmitAsync(Encoding.UTF8.GetBytes("audio"), "audio/wav", default)).Should().Be("job-123");
        Requests.Select(Request => Request.Path).Should().Equal("/v2/upload", "/v2/transcript");
        Requests[0].Body.Should().Be("audio");
        using var Body = JsonDocument.Parse(Requests[1].Body);
        Body.RootElement.GetProperty("speaker_labels").GetBoolean().Should().BeTrue();
        Body.RootElement.GetProperty("speech_models")[0].GetString().Should().Be("test-model");
    }

    [Theory]
    [InlineData("queued", TranscriptionStatus.Pending)]
    [InlineData("processing", TranscriptionStatus.Pending)]
    [InlineData("error", TranscriptionStatus.Failed)]
    public async Task Status_MapsToNeutralContract_WithoutVendorError(string Status, TranscriptionStatus Expected)
    {
        using var Handler = new StubHandler(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { status = Status, error = "private vendor details" }))));
        var Result = await Create(Handler).GetResultAsync(Guid.NewGuid(), "job", default);
        Result.Status.Should().Be(Expected);
        Result.Error.Should().NotContain("private vendor");
    }

    [Fact]
    public async Task Completion_MapsDiarizedSegments()
    {
        using var Handler = new StubHandler(_ => Task.FromResult(Json("""
            {"status":"completed","utterances":[{"speaker":"B","start":12,"end":120,"text":"Hello","confidence":0.95}]}
            """)));
        var Result = await Create(Handler).GetResultAsync(Guid.NewGuid(), "job", default);
        Result.Status.Should().Be(TranscriptionStatus.Completed);
        Result.Segments.Should().Equal(new TranscriptSegment(1, 12, 120, "Hello", 0.95));
    }

    private static AssemblyAiTranscriptionService Create(HttpMessageHandler Handler)
    {
        var Factory = new Mock<IHttpClientFactory>();
        Factory.Setup(Value => Value.CreateClient("AssemblyAi")).Returns(new HttpClient(Handler) { BaseAddress = new Uri("https://provider.invalid/v2/") });
        return new(Factory.Object, Options.Create(new AssemblyAiOptions { SpeechModels = ["test-model"] }));
    }

    private static HttpResponseMessage Json(string Body) => new(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken) => Handler(Request);
    }
}
