using System.Text.Json;
using PersonalAgent.Contracts.Messaging.Responses;
using FluentAssertions;
using Xunit;

namespace PersonalAgent.Contracts.Tests.Messaging;

public class TranscriptionResponseTests
{
    [Fact]
    public void LegacyCachedResult_DeserializesWithoutChannelMetadata()
    {
        var Id = Guid.NewGuid();
        var Json = $$"""
            {"JobId":"{{Id}}","Status":1,"Segments":[{"SpeakerLabel":0,"StartMs":1,"EndMs":2,"Text":"Legacy","Confidence":0.9}],"RetryAfterSeconds":4}
            """;

        var Result = JsonSerializer.Deserialize<TranscriptionResponse>(Json);

        Result.Should().NotBeNull();
        Result!.AudioChannels.Should().BeNull();
        Result.Segments.Should().ContainSingle().Which.AudioChannel.Should().BeNull();
    }
}
