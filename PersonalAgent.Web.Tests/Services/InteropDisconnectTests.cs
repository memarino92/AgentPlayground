using Microsoft.JSInterop;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Services;

public sealed class InteropDisconnectTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CircuitDisconnect_DuringImportOrInvocation_DoesNotBreakTeardown(bool ImportSucceeds)
    {
        var JS = new DisconnectedRuntime(ImportSucceeds);
        await using var Menu = new MenuInterop(JS);
        await using var Links = new EvidenceLinksInterop(JS);
        await using var Player = new EvidencePlayerInterop(JS);

        await Menu.InitializeAsync(default);
        await Links.InitializeAsync(default);
        await Player.InitializeAsync(default, 4000);
        await Player.StopAsync(default);
    }

    private sealed class DisconnectedRuntime(bool ImportSucceeds) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string Identifier, object?[]? Args) =>
            ImportSucceeds
                ? ValueTask.FromResult((TValue)(object)new DisconnectedModule())
                : ValueTask.FromException<TValue>(new JSDisconnectedException("Synthetic disconnect"));

        public ValueTask<TValue> InvokeAsync<TValue>(string Identifier, CancellationToken CancellationToken, object?[]? Args) =>
            ImportSucceeds
                ? ValueTask.FromResult((TValue)(object)new DisconnectedModule())
                : ValueTask.FromException<TValue>(new JSDisconnectedException("Synthetic disconnect"));
    }

    private sealed class DisconnectedModule : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string Identifier, object?[]? Args) =>
            ValueTask.FromException<TValue>(new JSDisconnectedException("Synthetic disconnect"));

        public ValueTask<TValue> InvokeAsync<TValue>(string Identifier, CancellationToken CancellationToken, object?[]? Args) =>
            ValueTask.FromException<TValue>(new JSDisconnectedException("Synthetic disconnect"));

        public ValueTask DisposeAsync() => ValueTask.FromException(new JSDisconnectedException("Synthetic disconnect"));
    }
}
