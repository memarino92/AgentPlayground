using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace PersonalAgent.Web.Services;

internal sealed class EvidenceLinksInterop(IJSRuntime JS) : IAsyncDisposable
{
    private IJSObjectReference? Module;
    private ElementReference Root;

    public async ValueTask InitializeAsync(ElementReference Element)
    {
        Root = Element;
        Module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/ChatMessageList.razor.js");
        await Module.InvokeVoidAsync("initialize", Root);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Module is null) return;
            await Module.InvokeVoidAsync("dispose", Root);
            await Module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
