using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class ScheduledJobs
{
    [Inject] private PersonalAgentClient Api { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private IJSRuntime Js { get; set; } = null!;
    [Inject] private ILogger<ScheduledJobs> Logger { get; set; } = null!;
    [SupplyParameterFromQuery(Name = "jobId")] public Guid? JobId { get; set; }
    private static readonly string[] States = ["Scheduled", "Running", "Retrying", "Completed", "Blocked", "NeedsReview", "Failed", "Cancelled"];
    private readonly CancellationTokenSource Lifetime = new();
    private IReadOnlyList<string> Profiles = [];
    private IReadOnlyList<ScheduledJobResponse> Jobs = [];
    private ScheduledJobDetailResponse? Selected;
    private TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private string ProfileId = "";
    private string Status = "";
    private string? Error;
    private string? Notice;
    private DateTimeOffset? Before;
    private bool Busy;
    private bool Initialized;
    private bool QueryChanged;
    private Guid? LoadedJobId;
    private Task? PollTask;

    protected override void OnParametersSet()
    {
        if (LoadedJobId == JobId) return;
        LoadedJobId = JobId;
        QueryChanged = true;
    }

    protected override async Task OnAfterRenderAsync(bool FirstRender)
    {
        if (FirstRender)
        {
            try { Zone = await JobTimeZone.ReadAsync(Js, Lifetime.Token); }
            catch (JSException) { /* UTC is explicitly labelled if the browser cannot supply its zone. */ }
            await LoadInitialAsync();
            PollTask = PollAsync();
            StateHasChanged();
        }
        else if (QueryChanged && Initialized && !Busy)
        {
            QueryChanged = false;
            if (JobId is { } Id) await SelectAsync(Id);
            StateHasChanged();
        }
    }

    private async Task LoadInitialAsync()
    {
        await LoadAsync(async () =>
        {
            var User = (await Authentication.GetAuthenticationStateAsync()).User;
            Profiles = User.IsInRole("Owner")
                ? User.FindFirst("urn:github:login")?.Value is { } Owner ? [Owner] : []
                : await Api.GetAssignedProfilesAsync($"google:{User.FindFirst(ClaimTypes.NameIdentifier)?.Value}", User.FindFirst(ClaimTypes.Email)?.Value ?? "");
            ProfileId = Profiles.FirstOrDefault() ?? "";
            if (JobId is { } Id)
            {
                Selected = await Api.GetJobAsync(Id, Lifetime.Token);
                if (Selected is not null) ProfileId = Selected.Job.SubjectProfileId;
                else Notice = "This job is unavailable or you no longer have access.";
            }
            QueryChanged = false;
            await ReadAsync();
        });
        Initialized = true;
    }

    private async Task ReadAsync()
    {
        if (string.IsNullOrWhiteSpace(ProfileId)) return;
        Jobs = await Api.GetJobsAsync(ProfileId, Status, Before, Lifetime.Token);
        if (Selected is not null) Selected = await Api.GetJobAsync(Selected.Job.TaskId, Lifetime.Token);
    }

    private Task RefreshAsync() => Initialized ? LoadAsync(ReadAsync) : LoadInitialAsync();
    private Task SelectAsync(Guid Id) => LoadAsync(async () =>
    {
        Selected = await Api.GetJobAsync(Id, Lifetime.Token);
        if (Selected is null) Notice = "This job is unavailable or you no longer have access.";
        else if (ProfileId != Selected.Job.SubjectProfileId)
        {
            ProfileId = Selected.Job.SubjectProfileId;
            Before = null;
            await ReadAsync();
        }
    });
    private async Task ChangeProfileAsync(ChangeEventArgs Args)
    {
        ProfileId = Args.Value?.ToString() ?? "";
        Selected = null;
        Before = null;
        await RefreshAsync();
    }
    private async Task ChangeStatusAsync(ChangeEventArgs Args)
    {
        Status = Args.Value?.ToString() ?? "";
        Before = null;
        await RefreshAsync();
    }
    private async Task OlderAsync() { Before = Jobs.Last().CreatedAt; await RefreshAsync(); }
    private async Task NewestAsync() { Before = null; await RefreshAsync(); }
    private Task CancelAsync() => LoadAsync(async () =>
    {
        if (Selected is null) return;
        Notice = await Api.CancelJobAsync(Selected.Job.TaskId, Lifetime.Token)
            ? "Job cancelled." : "This job has already started or finished and cannot be cancelled.";
        await ReadAsync();
    });

    private async Task LoadAsync(Func<Task> Action)
    {
        if (Busy || Lifetime.IsCancellationRequested) return;
        Busy = true;
        Error = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception Exception)
        {
            Logger.LogWarning(Exception, "Scheduled jobs view could not refresh");
            Jobs = [];
            Selected = null;
            Error = "Unable to load jobs. Check your access and try Refresh.";
        }
        finally { Busy = false; }
    }

    private async Task PollAsync()
    {
        try
        {
            while (!Lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), Lifetime.Token);
                await InvokeAsync(async () => { await RefreshAsync(); StateHasChanged(); });
            }
        }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
    }

    private string Format(DateTimeOffset Value) => TimeZoneInfo.ConvertTime(Value, Zone).ToString("MMM d, yyyy h:mm:ss tt");
    private static string Label(string Value) => Value == "NeedsReview" ? "Needs review" : Value;
    private static string ChatLink(string SessionId, string Subject) => $"/chat?sessionId={Uri.EscapeDataString(SessionId)}&profileId={Uri.EscapeDataString(Subject)}";
    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync();
        if (PollTask is not null) await PollTask;
        Lifetime.Dispose();
    }
}

internal static class JobTimeZone
{
    public static async Task<TimeZoneInfo> ReadAsync(IJSRuntime Js, CancellationToken Token)
    {
        var Module = await Js.InvokeAsync<IJSObjectReference>("import", Token, "./Components/Pages/ScheduledJobs.razor.js");
        try
        {
            var Id = await Module.InvokeAsync<string>("timeZone", Token);
            return TimeZoneInfo.TryFindSystemTimeZoneById(Id, out var Zone) ? Zone : TimeZoneInfo.Utc;
        }
        finally
        {
            try { await Module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
