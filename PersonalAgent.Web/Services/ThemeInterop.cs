using Microsoft.JSInterop;

using PersonalAgent.Web.Components;

namespace PersonalAgent.Web.Services;

internal sealed class ThemeInterop(IJSRuntime JS) : IAsyncDisposable
{
    internal const string ModulePath = "./Components/ThemePicker.razor.js";
    private const string InitializeMethod = "initialize";
    private const string SetMethod = "setPreference";
    private const string DisposeMethod = "dispose";
    private IJSObjectReference? Module;
    private IJSObjectReference? Controller;

    public async ValueTask InitializeAsync(DotNetObjectReference<ThemePicker> Reference)
    {
        try
        {
            Module = await JS.InvokeAsync<IJSObjectReference>("import", ModulePath);
            Controller = await Module.InvokeAsync<IJSObjectReference>(InitializeMethod, Reference);
        }
        catch (JSDisconnectedException) { }
    }

    public async ValueTask SetAsync(string Preference)
    {
        try { if (Controller is not null) await Controller.InvokeVoidAsync(SetMethod, Preference); }
        catch (JSDisconnectedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Controller is not null)
            {
                await Controller.InvokeVoidAsync(DisposeMethod);
                await Controller.DisposeAsync();
            }
            if (Module is not null) await Module.DisposeAsync();
        }
        catch (JSDisconnectedException) { }
    }
}
