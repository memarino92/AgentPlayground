using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class CodingJobsPage
{
    [Inject] private PersonalAgentClient Client { get; set; } = default!;
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private IReadOnlyList<CodingJobView> Items = [];
    private readonly Dictionary<Guid, CodingArtifact> Artifacts = [];
    private string Profile = "";
    private string? Error;
    private bool Loaded, Busy;
    private Task? Poll;
    protected override async Task OnAfterRenderAsync(bool FirstRender)
    {
        if (!FirstRender) return;
        Profile = (await AuthenticationState).User.FindFirst("urn:github:login")?.Value ?? "";
        await RefreshAsync();
        Poll = PollAsync();
        StateHasChanged();
    }
    private Task RefreshAsync() => RunAsync(async () => { Items = await Client.GetCodingJobsAsync(Profile, Lifetime.Token); Loaded = true; });
    private Task CancelAsync(Guid Id) => RunAsync(async () =>
    {
        await Client.CancelCodingJobAsync(Profile, Id, Lifetime.Token);
        Items = await Client.GetCodingJobsAsync(Profile, Lifetime.Token);
    });
    private Task LoadArtifactAsync(Guid Id) => RunAsync(async () =>
    {
        var Job = await Client.GetCodingJobAsync(Profile, Id, Lifetime.Token);
        if (Job.Artifact is { } Artifact) Artifacts[Id] = Artifact;
        else Error = "No artifact is available for this job yet.";
    });
    private async Task RunAsync(Func<Task> Action)
    {
        if (Busy || string.IsNullOrEmpty(Profile)) return;
        Busy = true; Error = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (HttpRequestException) { Error = "Jobs are unavailable or the operation is no longer allowed. Refresh and check your access."; }
        finally { Busy = false; }
    }
    private async Task PollAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), Lifetime.Token);
                await InvokeAsync(async () => { await RefreshAsync(); StateHasChanged(); });
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync();
        if (Poll is not null) await Poll;
        Lifetime.Dispose();
    }
}
