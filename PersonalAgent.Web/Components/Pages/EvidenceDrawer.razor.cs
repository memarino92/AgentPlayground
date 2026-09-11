using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class EvidenceDrawer
{
    [Inject] private PersonalAgentClient Client { get; set; } = default!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Parameter] public Guid UploadId { get; set; }
    [Parameter] public string ProfileId { get; set; } = string.Empty;
    [Parameter] public int? StartMs { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private CoachEvidenceResponse? Evidence;
    private ElementReference Root;
    private EvidencePlayerInterop? Player;
    private CancellationTokenSource? LoadCancellation;
    private bool Loading, IsOwner, ConfirmDelete, Deleting, InitializePlayer;
    private string? Error;
    private (Guid, string, int?)? LoadedSource;
    private string AudioUrl => $"/media/coach-checkins/{UploadId}/audio?profileId={Uri.EscapeDataString(ProfileId)}";

    protected override async Task OnParametersSetAsync()
    {
        var Source = (UploadId, ProfileId, StartMs);
        if (LoadedSource == Source) return;
        LoadedSource = Source;
        LoadCancellation?.Cancel();
        LoadCancellation?.Dispose();
        var Cancellation = LoadCancellation = new();
        Loading = true;
        Evidence = null;
        Error = null;
        ConfirmDelete = false;
        try
        {
            IsOwner = (await Authentication.GetAuthenticationStateAsync()).User.IsInRole("Owner");
            var Result = await Client.GetCoachEvidenceAsync(UploadId, ProfileId, Cancellation.Token);
            if (Cancellation.IsCancellationRequested) return;
            Evidence = Result;
            InitializePlayer = true;
        }
        catch (OperationCanceledException) when (Cancellation.IsCancellationRequested) { }
        catch (HttpRequestException) { if (!Cancellation.IsCancellationRequested) Error = "Evidence could not be loaded. Access may have changed; close and try again."; }
        finally { if (!Cancellation.IsCancellationRequested) Loading = false; }
    }

    protected override async Task OnAfterRenderAsync(bool FirstRender)
    {
        if (!InitializePlayer || Loading) return;
        InitializePlayer = false;
        Player ??= new(JS);
        await Player.InitializeAsync(Root, StartMs);
    }

    private async Task DeleteAsync()
    {
        Deleting = true;
        try
        {
            await Client.DeleteCoachAudioAsync(UploadId, ProfileId);
            if (Player is not null) await Player.StopAsync(Root);
            Evidence = Evidence! with { AudioAvailable = false };
            ConfirmDelete = false;
        }
        catch (HttpRequestException) { Error = "Recording could not be deleted. Close and reopen the evidence to check its current state."; }
        finally { Deleting = false; }
    }

    private static string Timestamp(int Milliseconds) => TimeSpan.FromMilliseconds(Milliseconds).ToString(@"hh\:mm\:ss");
    private Task HandleKeyDown(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs Event) => Event.Key == "Escape" ? OnClose.InvokeAsync() : Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        LoadCancellation?.Cancel();
        LoadCancellation?.Dispose();
        if (Player is not null) await Player.DisposeAsync();
    }
}
