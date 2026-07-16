using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using FluentAssertions;
using MassTransit;
using Moq;
using PersonalAgent.Worker.Models;
using PersonalAgent.Worker.Services;
using Xunit;

namespace PersonalAgent.Worker.Tests.Services;

public class CoachTranscriptProcessingServiceTests
{
    [Fact]
    public async Task ProcessAsync_ReturnsSummaryAndChunkMetadata()
    {
        var embeddingClient = new Mock<IRequestClient<GenerateEmbeddingsRequest>>();
        var responseMessage = new GenerateEmbeddingsResponse(
            [[0.1f, 0.2f, 0.3f], [0.3f, 0.2f, 0.1f]],
            3);
        var response = new Mock<Response<GenerateEmbeddingsResponse>>();
        response.SetupGet(value => value.Message).Returns(responseMessage);

        embeddingClient
            .Setup(client => client.GetResponse<GenerateEmbeddingsResponse>(It.IsAny<GenerateEmbeddingsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response.Object);

        var service = new CoachTranscriptProcessingService(embeddingClient.Object);
        var utterances = new List<TranscribedUtterance>
        {
            new(0, "coach", 0, 1000, "Keep your squat braced and drive up.", 0.92),
            new(1, "athlete", 1001, 2000, "I felt my back collapse at the bottom.", 0.93),
            new(0, "coach", 2001, 3000, "Use the cue chest tall and push knees out.", 0.95),
            new(1, "athlete", 3001, 4200, "Got it, will apply that next set.", 0.88),
            new(0, "coach", 4201, 5200, "Bench setup looked tighter this week.", 0.9)
        };

        var result = await service.ProcessAsync(Guid.NewGuid(), utterances);

        result.Chunks.Should().HaveCount(2);
        result.Chunks[0].SpeakerMix.Should().Be("mixed");
        result.Chunks[0].ExerciseTags.Should().Contain("squat");
        result.Chunks[0].Embedding.Should().HaveCount(3);
        result.SummaryMarkdown.Should().Contain("Coach Check-In Summary");
        result.SummaryJson.Should().Contain("chunkCount");
        embeddingClient.Verify(client => client.GetResponse<GenerateEmbeddingsResponse>(It.IsAny<GenerateEmbeddingsRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_ThrowsWhenEmbeddingCountDoesNotMatchChunkCount()
    {
        var embeddingClient = new Mock<IRequestClient<GenerateEmbeddingsRequest>>();
        var responseMessage = new GenerateEmbeddingsResponse([[0.1f, 0.2f, 0.3f]], 3);
        var response = new Mock<Response<GenerateEmbeddingsResponse>>();
        response.SetupGet(value => value.Message).Returns(responseMessage);

        embeddingClient
            .Setup(client => client.GetResponse<GenerateEmbeddingsResponse>(It.IsAny<GenerateEmbeddingsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response.Object);

        var service = new CoachTranscriptProcessingService(embeddingClient.Object);
        var utterances = new List<TranscribedUtterance>
        {
            new(0, "coach", 0, 1000, "squat cue", 0.9),
            new(1, "athlete", 1001, 2000, "response", 0.9),
            new(0, "coach", 2001, 3000, "bench cue", 0.9),
            new(1, "athlete", 3001, 4000, "response", 0.9),
            new(0, "coach", 4001, 5000, "deadlift cue", 0.9)
        };

        var act = async () => await service.ProcessAsync(Guid.NewGuid(), utterances);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Embedding count mismatch*");
    }
}
