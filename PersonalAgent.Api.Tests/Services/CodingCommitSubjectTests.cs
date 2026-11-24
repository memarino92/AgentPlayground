using FluentAssertions;
using Xunit;

using PersonalAgent.Api.Coding;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CodingCommitSubjectTests
{
    [Theory]
    [InlineData("feat: implement descriptive commit messages for platform improvements", "feat: implement descriptive commit messages for platform improvements")]
    [InlineData("fix(coding): preserve descriptive commit subjects\n\nTests passed.", "fix(coding): preserve descriptive commit subjects")]
    [InlineData("**docs: explain trusted PR publication**", "docs: explain trusted PR publication")]
    [InlineData("Add descriptive commit messages.\nValidation passed.", "feat: Add descriptive commit messages")]
    public void UsesImplementedChangeAndPreservesConventionalPrefix(string Summary, string Expected)
        => CodingCommitSubject.Create(Job(Summary)).Should().Be(Expected);

    [Theory]
    [InlineData("feat: implement platform improvement 771401786b464b46be659778faeb8349")]
    [InlineData("fix: revise platform improvement (771401786b464b46be659778faeb8349)")]
    [InlineData("Implements the requested platform change. Review the diff and recorded test results.")]
    public void ReplacesGenericOrIdentifierBasedSubjectsWithRequestedChange(string Summary)
    {
        var Candidate = Job(Summary) with { Instruction = "Improve generated commit messages for coding jobs" };
        CodingCommitSubject.Create(Candidate).Should().Be("feat: Improve generated commit messages for coding jobs");
    }

    [Theory]
    [InlineData("")]
    [InlineData("feat: run $(cat /private/key)")]
    [InlineData("feat: publish https://example.com/?token=secret")]
    [InlineData("feat: use sk-short-private-key")]
    [InlineData("feat: implement platform improvement 771401786b464b46be659778faeb8349")]
    public void FallsBackToChangedFileWithoutCopyingUnsafeText(string Summary)
        => CodingCommitSubject.Create(Job(Summary)).Should().Be("feat: update CodingCommitSubject.cs");

    [Fact]
    public void RevisionAndRetrySubjectsAreDeterministic()
    {
        var Candidate = Job("Handle missing coding summaries.") with { RootJobId = Guid.NewGuid() };
        CodingCommitSubject.Create(Candidate).Should().Be("fix: Handle missing coding summaries");
        CodingCommitSubject.Create(Candidate with { Id = Guid.NewGuid() }).Should().Be(CodingCommitSubject.Create(Candidate));
    }

    [Fact]
    public void FallbackDoesNotDependOnArtifactFileOrdering()
    {
        var Candidate = Job("") with { Artifact = new("", [new("Zebra.cs", ""), new("Alpha.cs", "")], [], "") };
        CodingCommitSubject.Create(Candidate).Should().Be("feat: update Alpha.cs");
        CodingCommitSubject.Create(Candidate with { Artifact = Candidate.Artifact with { Files = [..Candidate.Artifact.Files.Reverse()] } })
            .Should().Be(CodingCommitSubject.Create(Candidate));
    }

    [Fact]
    public void LongSummariesAndUnusablePathsHaveBoundedMeaningfulFallback()
    {
        var Candidate = Job(new string('a', 4000)) with { Artifact = new("", [], [], new string('a', 4000)) };
        CodingCommitSubject.Create(Candidate).Should().Be("feat: update application behavior and validation");
    }

    private static CodingJob Job(string Summary) => new()
    {
        Id = Guid.Parse("77140178-6b46-4b46-be65-9778faeb8349"),
        Artifact = new("", [new("PersonalAgent.Api/Coding/CodingCommitSubject.cs", "changed")], [], Summary)
    };
}
