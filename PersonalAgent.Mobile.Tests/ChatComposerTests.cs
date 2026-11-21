using PersonalAgent.Mobile.Services;
using Xunit;

namespace PersonalAgent.Mobile.Tests;

public class ChatComposerTests
{
    [Fact]
    public async Task ClearsImmediatelyBeforeStreamingAndRejectsDuplicateSends()
    {
        var Composer = new ChatComposer();
        var Draft = "  Send this  ";
        var Response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Sends = 0;
        var Sending = Composer.SendAsync(() => Draft, Text => Draft = Text, async Message =>
        {
            Sends++;
            Assert.Equal("Send this", Message);
            Assert.Empty(Draft);
            await Response.Task;
        });

        Assert.Empty(Draft);
        Assert.True(Composer.IsSending);
        Assert.False(Sending.IsCompleted);
        Draft = "Next draft";
        await Composer.SendAsync(() => Draft, Text => Draft = Text, _ => { Sends++; return Task.CompletedTask; });
        Assert.Equal(1, Sends);
        Assert.Equal("Next draft", Draft);

        Response.SetResult();
        await Sending;
        Assert.False(Composer.IsSending);
        Assert.Equal("Next draft", Draft);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureRestoresSubmittedDraftOnlyWhenThereIsNoNewDraft(bool NewDraft)
    {
        var Composer = new ChatComposer();
        var Draft = "  Retry this  ";
        var Response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Sending = Composer.SendAsync(() => Draft, Text => Draft = Text, _ => Response.Task);
        Assert.Empty(Draft);
        if (NewDraft) Draft = "Keep this new draft";
        var Error = new HttpRequestException("Delivery uncertain");
        Response.SetException(Error);

        Assert.Same(Error, await Assert.ThrowsAsync<HttpRequestException>(() => Sending));
        Assert.False(Composer.IsSending);
        Assert.Equal(NewDraft ? "Keep this new draft" : "  Retry this  ", Draft);

        // After the caller has refreshed to check delivery, sending is available again.
        await Composer.SendAsync(() => Draft, Text => Draft = Text, Message =>
        {
            Assert.Equal(NewDraft ? "Keep this new draft" : "Retry this", Message);
            return Task.CompletedTask;
        });
        Assert.Empty(Draft);
    }

    [Fact]
    public async Task StreamUpdatesAndCompletionDoNotEraseASubsequentDraft()
    {
        var Composer = new ChatComposer();
        var Draft = "First message";
        var FirstDelta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ContinueStreaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Reply = string.Empty;
        var Sending = Composer.SendAsync(() => Draft, Text => Draft = Text, async _ =>
        {
            Reply += "Partial ";
            FirstDelta.SetResult();
            await ContinueStreaming.Task;
            Reply += "response";
        });

        await FirstDelta.Task;
        Assert.Empty(Draft);
        Assert.Equal("Partial ", Reply);
        Assert.False(Sending.IsCompleted);
        Draft = "A new message while streaming";
        ContinueStreaming.SetResult();
        await Sending;
        Assert.Equal("Partial response", Reply);
        Assert.Equal("A new message while streaming", Draft);
    }

    [Fact]
    public async Task FailureBeforeStreamingRestoresTheUntrimmedDraft()
    {
        var Composer = new ChatComposer();
        var Draft = "  Keep my draft  ";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Composer.SendAsync(() => Draft, Text => Draft = Text,
            _ => throw new InvalidOperationException("Sign in to continue.")));
        Assert.Equal("  Keep my draft  ", Draft);
        Assert.False(Composer.IsSending);
    }

    [Fact]
    public async Task InterruptedStreamRestoresTheDraftAndReleasesTheSendGuard()
    {
        var Composer = new ChatComposer();
        var Draft = "Recover this";
        await Assert.ThrowsAsync<TaskCanceledException>(() => Composer.SendAsync(() => Draft, Text => Draft = Text,
            _ => Task.FromCanceled(new CancellationToken(true))));
        Assert.Equal("Recover this", Draft);
        Assert.False(Composer.IsSending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task EmptyDraftIsNotAcceptedOrCleared(string? Draft)
    {
        var Composer = new ChatComposer();
        await Composer.SendAsync(() => Draft, _ => Assert.Fail("Draft should not change"),
            _ => throw new InvalidOperationException("Should not send"));
        Assert.False(Composer.IsSending);
    }
}
