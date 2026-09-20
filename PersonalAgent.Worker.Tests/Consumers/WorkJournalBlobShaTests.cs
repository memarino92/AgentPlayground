using System.Text;

using FluentAssertions;
using PersonalAgent.Worker.Consumers;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public class WorkJournalBlobShaTests
{
    [Theory]
    [InlineData("", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391")]
    [InlineData("hello", "b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0")]
    public void GetBlobSha_MatchesKnownGitObjects(string Content, string ExpectedSha)
        => SyncWorkJournalConsumer.GetBlobSha(Encoding.UTF8.GetBytes(Content)).Should().Be(ExpectedSha);
}
