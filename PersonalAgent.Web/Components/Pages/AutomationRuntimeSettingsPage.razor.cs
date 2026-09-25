using System.Net;
using Microsoft.AspNetCore.Components;
using PersonalAgent.Contracts.Automations;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class AutomationRuntimeSettingsPage : IAsyncDisposable
{
    [Inject] private PersonalAgentClient Client { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private FormModel Model = new();
    private long Revision;
    private bool Busy, Loaded, HasToken;
    private string? Error, Notice;
    protected override Task OnInitializedAsync() => LoadAsync();
    private Task LoadAsync() => RunAsync(async () => Apply(await Client.GetAutomationRuntimeAsync(Lifetime.Token)));
    private Task SaveAsync() => RunAsync(async () =>
    {
        var Settings = new AutomationRuntimeSettings { Enabled = Model.Enabled, EnvironmentId = Model.EnvironmentId.Trim(), Checkpoint = Model.Checkpoint.Trim(),
            ImageId = Model.ImageId.Trim(), GatewayUrl = Model.GatewayUrl.Trim(), ReviewMode = Model.ReviewMode,
            ApprovedPackages = Model.Packages.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) };
        Apply(await Client.SaveAutomationRuntimeAsync(new(Revision, Settings, Model.TokenAction, Model.TokenAction == "replace" ? Model.Token : null), Lifetime.Token));
        Notice = "Saved. New runs and gateway requests use these settings without a restart.";
    });
    private void Apply(AutomationRuntimeView View)
    {
        Revision = View.Revision; HasToken = View.HasToken; Loaded = true;
        Model = new() { Enabled = View.Settings.Enabled, EnvironmentId = View.Settings.EnvironmentId, Checkpoint = View.Settings.Checkpoint,
            ImageId = View.Settings.ImageId, GatewayUrl = View.Settings.GatewayUrl, ReviewMode = View.Settings.ReviewMode, Packages = string.Join('\n', View.Settings.ApprovedPackages) };
    }
    private async Task RunAsync(Func<Task> Action)
    {
        if (Busy) return;
        Busy = true; Error = Notice = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (HttpRequestException E)
        {
            Error = E.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Deployment administrator access is required.",
                HttpStatusCode.Conflict => "Settings changed in another session. Reload before saving.",
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "Deploy the matching API revision to use runner setup.",
                HttpStatusCode.BadRequest => "Check the environment UUID, checkpoint, image SHA-256, HTTPS origin, exact package versions and token action. Disable execution before clearing its token.",
                _ => "Runner settings are unavailable. Check the API and encrypted database configuration."
            };
            if (E.StatusCode == HttpStatusCode.Forbidden) { Loaded = false; Model = new(); }
        }
        catch (Exception) { Error = "Runner settings are unavailable. Check the API and retry."; }
        finally { Model.Token = ""; Busy = false; }
    }
    public async ValueTask DisposeAsync() { await Lifetime.CancelAsync(); Lifetime.Dispose(); }
    private sealed class FormModel
    {
        public bool Enabled { get; set; }
        public string EnvironmentId { get; set; } = "";
        public string Checkpoint { get; set; } = "";
        public string ImageId { get; set; } = "";
        public string GatewayUrl { get; set; } = "";
        public string ReviewMode { get; set; } = "Off";
        public string Packages { get; set; } = "";
        public string TokenAction { get; set; } = "keep";
        public string Token { get; set; } = "";
    }
}
