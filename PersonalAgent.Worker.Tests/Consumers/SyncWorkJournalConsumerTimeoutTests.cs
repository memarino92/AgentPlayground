using FluentAssertions;
using PersonalAgent.Worker.Consumers;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public class SyncWorkJournalConsumerTimeoutTests
{
    [Fact]
    public async Task RequestWithTimeoutAsync_DoesNotOverflowOrHang_WhenUsingReasonableTimeout()
    {
        var response = await SyncWorkJournalConsumer.RequestWithTimeoutAsync(
            _ => Task.FromResult("ok"),
            CancellationToken.None,
            TimeSpan.FromSeconds(2));

        response.Should().Be("ok");
    }

    [Fact]
    public async Task RequestWithTimeoutAsync_Cancels_WhenRequestExceedsTimeout()
    {
        var act = async () => await SyncWorkJournalConsumer.RequestWithTimeoutAsync(
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                return "never";
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(50));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
