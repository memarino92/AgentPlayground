using System.Net;
using AgentPlayground.Integrations;
using Microsoft.AspNetCore.Components;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class IntegrationSettings : IAsyncDisposable
{
    [Parameter] public bool OpenTelemetry { get; set; }
    [Inject] private PersonalAgentClient ApiClient { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private IntegrationSettingsResponse? Settings;
    private IntegrationTestResponse? TestResult;
    private bool Busy;
    private string? Error;
    private string? Notice;
    private int FormGeneration;

    protected override Task OnInitializedAsync() => RefreshAsync();

    private Task RefreshAsync() => RunAsync(async () =>
    {
        Settings = await (OpenTelemetry ? ApiClient.GetOtelSettingsAsync(Lifetime.Token) : ApiClient.GetIntegrationSettingsAsync(Lifetime.Token));
        FormGeneration++;
    });

    private Task SaveAsync(SaveIntegrationRequest Request) => RunAsync(async () =>
    {
        Settings = await (OpenTelemetry ? ApiClient.SaveOtelSettingsAsync(Request, Lifetime.Token) : ApiClient.SaveIntegrationSettingsAsync(Request, Lifetime.Token));
        Notice = "Validated and saved. Apply this revision when ready.";
        TestResult = null;
    });

    private Task ApplyAsync() => RunAsync(async () =>
    {
        Settings = await (OpenTelemetry ? ApiClient.ApplyOtelSettingsAsync(Settings!.SavedRevision, Lifetime.Token) : ApiClient.ApplyIntegrationSettingsAsync(Settings!.SavedRevision, Lifetime.Token));
        Notice = "Revision selected for application. Check each service below; refresh after 15 seconds.";
        TestResult = null;
    });

    private Task ReloadAsync() => RunAsync(async () =>
    {
        Settings = await (OpenTelemetry ? ApiClient.ReloadOtelSettingsAsync(Lifetime.Token) : ApiClient.ReloadIntegrationSettingsAsync(Lifetime.Token));
        Notice = "API reload attempted. Other services reconcile automatically.";
    });

    private Task TestAsync() => RunAsync(async () => TestResult = await ApiClient.TestIntegrationSettingsAsync(Settings!.ActiveRevision, Lifetime.Token));

    private async Task RunAsync(Func<Task> Action)
    {
        if (Busy) return;
        Busy = true;
        Error = Notice = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (HttpRequestException Exception)
        {
            Error = Exception.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Deployment administrator access is required. Configure INTEGRATION_SETTINGS_ADMINISTRATORS on the API host.",
                HttpStatusCode.Conflict => "Settings changed in another session. Refresh and review before retrying.",
                HttpStatusCode.BadRequest => "Settings failed server validation. Check the fields and retry.",
                _ => "Settings service is unavailable. Check bootstrap configuration or retry shortly."
            };
        }
        catch (Exception) { Error = "Unable to complete the settings operation. Retry shortly."; }
        finally { Busy = false; }
    }

    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync();
        Lifetime.Dispose();
    }
}
