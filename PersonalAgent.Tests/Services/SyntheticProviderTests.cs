using AgentPlayground.Contracts.Messaging.Responses;
using FluentAssertions;
using PersonalAgent.Development;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class SyntheticProviderTests
{
    [Fact]
    public void Embeddings_AreStableNormalizedAndDistinguishDifferentContent()
    {
        var First = SyntheticEmbeddingService.Embed("Deadlift bracing cue");
        First.Should().HaveCount(1536).And.Equal(SyntheticEmbeddingService.Embed("DEADLIFT bracing cue"));
        First.Sum(Value => (double)Value * Value).Should().BeApproximately(1, 0.00001);
        First.Should().NotEqual(SyntheticEmbeddingService.Embed("unrelated programming journal"));
        SyntheticEmbeddingService.Embed("").Sum(Value => Value * Value).Should().Be(1);
    }

    [Fact]
    public async Task Transcription_CanResumeOnAnotherProviderInstanceWithoutAudioOrNetwork()
    {
        var Id = Guid.NewGuid();
        var ProviderId = await new SyntheticTranscriptionProvider().SubmitAsync([1, 2, 3], "audio/mp4", default);
        var Result = await new SyntheticTranscriptionProvider().GetResultAsync(Id, ProviderId, default);
        Result.JobId.Should().Be(Id);
        Result.Status.Should().Be(TranscriptionStatus.Completed);
        Result.Segments.Select(Segment => Segment.SpeakerLabel).Distinct().Should().HaveCount(2);
        Result.Segments.Should().Contain(Segment => Segment.Text.Contains("Brace"));
    }
}
