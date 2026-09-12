using FluentAssertions;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class CoachEvidenceLinksTests
{
    private const string Path = "/evidence/11111111-1111-1111-1111-111111111111?profileId=athlete&startMs=995";

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://evidence")]
    public void RepairsOnlyReturnedEvidence(string Host)
    {
        var Destination = Host == "https://evidence" ? Host + Path[9..] : Host + Path;
        CoachEvidenceLinks.Normalize($"[source]({Destination})", [$"[Call evidence]({Path})"])
            .Should().Be($"[source]({Path})");
        CoachEvidenceLinks.Normalize($"[source]({Destination})", [])
            .Should().Be($"[source]({Destination})");
    }

    [Theory]
    [InlineData("https://evidence.")]
    [InlineData("https://www.evidence.")]
    public void RepairsIdMistakenForHostnameOnlyWhenEvidenceWasReturned(string Prefix)
    {
        var Answer = $"[source]({Prefix}{Path[10..]})";
        CoachEvidenceLinks.Normalize(Answer, [Path]).Should().Be($"[source]({Path})");
        CoachEvidenceLinks.Normalize(Answer, []).Should().Be(Answer);
    }

    [Fact]
    public void EscapedSlashes_AreRepairedOnlyForReturnedDestinations()
    {
        var Escaped = Path.Replace("/", "\\/", StringComparison.Ordinal);
        CoachEvidenceLinks.Normalize($"[source]({Escaped})", [Path]).Should().Be($"[source]({Path})");
        CoachEvidenceLinks.Normalize($"[source]({Escaped})", []).Should().Be($"[source]({Escaped})");
    }

    [Theory]
    [InlineData("startMs=995", "startMs=996")]
    [InlineData("profileId=athlete", "profileId=other")]
    public void DoesNotBlessUnreturnedTimingOrSubject(string From, string To)
    {
        var Answer = $"[source](https://example.com{Path.Replace(From, To)})";
        CoachEvidenceLinks.Normalize(Answer, [Path]).Should().Be(Answer);
    }
}
