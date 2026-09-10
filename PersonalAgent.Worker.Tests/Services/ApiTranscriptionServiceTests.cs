using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using FluentAssertions;
using MassTransit;
using Moq;
using PersonalAgent.Worker.Services;
using Xunit;

namespace PersonalAgent.Worker.Tests.Services;

public class ApiTranscriptionServiceTests
{
    [Fact]
    public async Task Completion_UsesUploadReference_AndLeavesRoleAttributionToDomain()
    {
        var Id = Guid.NewGuid();
        var Response = new Mock<Response<TranscriptionResponse>>();
        Response.SetupGet(Value => Value.Message).Returns(new TranscriptionResponse(Id, TranscriptionStatus.Completed, [new(1, 10, 20, "Synthetic", 0.9)]));
        var Client = new Mock<IRequestClient<TranscriptionRequest>>();
        Client.Setup(Value => Value.GetResponse<TranscriptionResponse>(new TranscriptionRequest(Id, "owner"), It.IsAny<CancellationToken>())).ReturnsAsync(Response.Object);
        var Result = await new ApiTranscriptionService(Client.Object).TranscribeAsync(Id, "owner");
        Result.Should().ContainSingle();
        Result[0].SpeakerRole.Should().Be("unknown");
        Result[0].SpeakerLabel.Should().Be(1);
        Result[0].Text.Should().Be("Synthetic");
        Client.VerifyAll();
    }

    [Fact]
    public async Task FailedJob_PropagatesNeutralFailure()
    {
        var Id = Guid.NewGuid();
        var Response = new Mock<Response<TranscriptionResponse>>();
        Response.SetupGet(Value => Value.Message).Returns(new TranscriptionResponse(Id, TranscriptionStatus.Failed, [], "Transcription timed out."));
        var Client = new Mock<IRequestClient<TranscriptionRequest>>();
        Client.Setup(Value => Value.GetResponse<TranscriptionResponse>(It.IsAny<TranscriptionRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(Response.Object);
        var Act = () => new ApiTranscriptionService(Client.Object).TranscribeAsync(Id, "owner");
        await Act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Transcription timed out.");
    }

    [Fact]
    public async Task PendingJob_ObservesCancellation()
    {
        var Id = Guid.NewGuid();
        using var Cancellation = new CancellationTokenSource();
        var Response = new Mock<Response<TranscriptionResponse>>();
        Response.SetupGet(Value => Value.Message).Returns(new TranscriptionResponse(Id, TranscriptionStatus.Pending, []));
        var Client = new Mock<IRequestClient<TranscriptionRequest>>();
        Client.Setup(Value => Value.GetResponse<TranscriptionResponse>(It.IsAny<TranscriptionRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => Cancellation.Cancel()).ReturnsAsync(Response.Object);
        var Act = () => new ApiTranscriptionService(Client.Object).TranscribeAsync(Id, "owner", Cancellation.Token);
        await Act.Should().ThrowAsync<OperationCanceledException>();
    }
}
