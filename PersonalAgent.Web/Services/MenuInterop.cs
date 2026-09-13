using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PersonalAgent.Web.Services;

internal sealed class MenuInterop(IJSRuntime JS) : IAsyncDisposable
{
    private IJSObjectReference? Module;
    public async ValueTask InitializeAsync(ElementReference Root)
    {
        Module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Layout.razor.js");
        await Module.InvokeVoidAsync("initialize", Root);
    }
    public async ValueTask DisposeAsync()
    {
        try { if (Module is not null) await Module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
