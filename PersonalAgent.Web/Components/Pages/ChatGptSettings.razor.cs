using Microsoft.AspNetCore.Components;
using PersonalAgent.Integrations.ChatGpt;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class ChatGptSettings : IAsyncDisposable
{
    [Inject] private PersonalAgentClient Api { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private IReadOnlyList<ChatGptConnection> Connections = [];
    private ChatGptPairing? Pairing;
    private bool Loaded;
    private bool Busy;
    private string? Error;
    private string? Notice;
    private string InstanceUrl => new Uri(Navigation.BaseUri).GetLeftPart(UriPartial.Authority);

    protected override Task OnInitializedAsync() => RefreshAsync();
    private Task RefreshAsync() => RunAsync(async () =>
    {
        Connections = await Api.GetChatGptConnectionsAsync(Lifetime.Token);
        Loaded = true;
        Pairing = null;
    });
    private Task DismissWelcomeAsync(Guid Id) => RunAsync(async () =>
    {
        await Api.DismissChatGptWelcomeAsync(Id, Lifetime.Token);
        Connections = await Api.GetChatGptConnectionsAsync(Lifetime.Token);
    });
    private Task StartAsync(Guid? Id) => RunAsync(async () => Pairing = await Api.StartChatGptPairingAsync(Id, Lifetime.Token));
    private Task DisconnectAsync(Guid Id) => RunAsync(async () =>
    {
        var result = await Api.DisconnectChatGptAsync(Id, Lifetime.Token);
        Notice = result.RemoteRevocationConfirmed ? "ChatGPT disconnected." : "Disconnected locally. Remote revocation was not confirmed; disconnect this app in ChatGPT Settings.";
        Connections = await Api.GetChatGptConnectionsAsync(Lifetime.Token);
    });
    private async Task RunAsync(Func<Task> Action)
    {
        if (Busy) return;
        Busy = true;
        Error = Notice = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (HttpRequestException Exception)
        {
            Error = Exception.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed
                ? "Deploy the current API version before connecting ChatGPT."
                : "ChatGPT connections could not be updated. Check API availability and retry.";
        }
        finally { Busy = false; }
    }
    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync();
        Lifetime.Dispose();
    }
}
