using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PersonalAgent.Web.Services;

internal sealed class EvidencePlayerInterop(IJSRuntime JS) : IAsyncDisposable
{
    private IJSObjectReference? Module;
    public async ValueTask InitializeAsync(ElementReference Root, int? StartMs)
    {
        Module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/EvidenceDrawer.razor.js?v=2");
        await Module.InvokeVoidAsync("initialize", Root, StartMs);
    }
    public async ValueTask StopAsync(ElementReference Root)
    {
        if (Module is not null) await Module.InvokeVoidAsync("stop", Root);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (Module is not null) await Module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
