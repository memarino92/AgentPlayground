using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class Automations
{
    [Inject] private PersonalAgentClient Api { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;
    [Inject] private ILogger<Automations> Logger { get; set; } = null!;
    [CascadingParameter] private Task<AuthenticationState> AuthenticationState { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "automationId")] public Guid? AutomationId { get; set; }
    [SupplyParameterFromQuery(Name = "profileId")] public string? QueryProfile { get; set; }
    private readonly CancellationTokenSource Lifetime = new();
    private IReadOnlyList<string> Profiles = [];
    private IReadOnlyList<AutomationSummary> Items = [];
    private AutomationDetail? Selected;
    private AutomationRunDetail? SelectedRun;
    private string Profile = "";
    private string? Error;
    private string? Notice;
    private bool Initialized;
    private bool Busy;
    private int Offset;
    private int RunOffset;
    private Task? Poll;
    private string? LoadedQuery;

    protected override async Task OnParametersSetAsync()
    {
        if (!Initialized || LoadedQuery == Navigation.Uri) return;
        LoadedQuery = Navigation.Uri;
        if (Profiles.Contains(QueryProfile)) Profile = QueryProfile!;
        SelectedRun = null; RunOffset = 0;
        await RefreshAsync();
    }

    protected override async Task OnAfterRenderAsync(bool FirstRender)
    {
        if (!FirstRender) return;
        await LoadAsync(async () =>
        {
            var User = (await AuthenticationState).User;
            Profiles = User.IsInRole("Owner") ? User.FindFirst("urn:github:login")?.Value is { } Owner ? [Owner] : []
                : await Api.GetAssignedProfilesAsync($"google:{User.FindFirst(ClaimTypes.NameIdentifier)?.Value}", User.FindFirst(ClaimTypes.Email)?.Value ?? "", Lifetime.Token);
            Profile = Profiles.Contains(QueryProfile) ? QueryProfile! : Profiles.FirstOrDefault() ?? "";
            await ReadAsync();
        });
        Initialized = true;
        LoadedQuery = Navigation.Uri;
        Poll = PollAsync();
        StateHasChanged();
    }
    private async Task ReadAsync()
    {
        if (string.IsNullOrEmpty(Profile)) return;
        Items = await Api.GetAutomationsAsync(Profile, Offset, Lifetime.Token);
        Selected = AutomationId is { } Id ? await Api.GetAutomationAsync(Profile, Id, RunOffset, Lifetime.Token) : null;
        if (SelectedRun is not null && Selected is not null)
            SelectedRun = await Api.GetAutomationRunAsync(Profile, Selected.Automation.Id, SelectedRun.Run.Id, Lifetime.Token);
        else SelectedRun = null;
    }
    private Task RefreshAsync() => LoadAsync(ReadAsync);
    private Task SelectAsync(Guid Id) => LoadAsync(async () =>
    {
        AutomationId = Id; SelectedRun = null; RunOffset = 0;
        LoadedQuery = Navigation.GetUriWithQueryParameters(new Dictionary<string, object?> { ["automationId"] = Id, ["profileId"] = Profile });
        Navigation.NavigateTo(LoadedQuery, replace: true);
        await ReadAsync();
    });
    private Task SelectRunAsync(Guid RunId) => LoadAsync(async () =>
    {
        if (Selected is not null) SelectedRun = await Api.GetAutomationRunAsync(Profile, Selected.Automation.Id, RunId, Lifetime.Token);
    });
    private Task ControlAsync(string Operation) => LoadAsync(async () =>
    {
        if (Selected is null) return;
        await Api.ControlAutomationAsync(Profile, Selected.Automation.Id, Operation, Lifetime.Token);
        Notice = Operation == "run" ? "Run queued. Results will appear below." : Operation == "pause" ? "Future runs paused. Active runs can finish." : "Automation resumed.";
        await ReadAsync();
    });
    private async Task ChangeProfileAsync(ChangeEventArgs Args)
    {
        Profile = Args.Value?.ToString() ?? ""; AutomationId = null; Selected = null; SelectedRun = null; Offset = RunOffset = 0;
        Navigation.NavigateTo(Navigation.GetUriWithQueryParameters(new Dictionary<string, object?> { ["automationId"] = null, ["profileId"] = Profile }), replace: true);
        await RefreshAsync();
    }
    private async Task PageAsync(int Delta) { Offset = Math.Max(0, Offset + Delta); await RefreshAsync(); }
    private async Task RunPageAsync(int Delta) { RunOffset = Math.Max(0, RunOffset + Delta); await RefreshAsync(); }
    private async Task LoadAsync(Func<Task> Operation)
    {
        if (Busy || Lifetime.IsCancellationRequested) return;
        Busy = true; Error = null;
        try { await Operation(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (Exception Exception)
        {
            Logger.LogWarning("Automation dashboard request failed with {ExceptionType}", Exception.GetType().Name);
            Items = []; Selected = null; SelectedRun = null;
            Error = "Unable to load or change automations. Check your access and refresh.";
        }
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
    private static string Format(DateTimeOffset? Value) => Value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "—";
    private static string Pretty(string Source) => JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(Source), new JsonSerializerOptions { WriteIndented = true });
    public async ValueTask DisposeAsync() { await Lifetime.CancelAsync(); if (Poll is not null) await Poll; Lifetime.Dispose(); }
}
