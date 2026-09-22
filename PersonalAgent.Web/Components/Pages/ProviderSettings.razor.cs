using System.Net;
using Microsoft.AspNetCore.Components;
using PersonalAgent.Integrations;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class ProviderSettings : IAsyncDisposable
{
    [Inject] private PersonalAgentClient ApiClient { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private OpenRouterFormModel Model = new();
    private DatabaseSetting? OpenRouterSetting;
    private bool Busy;
    private bool Loaded;
    private string? Error;
    private string ErrorTitle = "Provider settings unavailable";
    private string? Notice;

    protected override Task OnInitializedAsync() => RefreshAsync();

    private Task RefreshAsync() => RunAsync(async () =>
    {
        var settings = await ApiClient.GetDatabaseSettingsAsync(Lifetime.Token);
        OpenRouterSetting = settings.SingleOrDefault(Setting => Setting.Scope == "Api"
            && string.Equals(Setting.Key, "OpenRouter:ApiKey", StringComparison.OrdinalIgnoreCase));
        Model = new();
        Loaded = true;
    }, "Couldn’t load provider settings");

    private Task SaveOpenRouterAsync() => RunAsync(async () =>
    {
        await ApiClient.SaveOpenRouterCredentialAsync(new(OpenRouterSetting?.Version, Model.ApiKey), Lifetime.Token);
        Model = new();
        var status = await ApiClient.ReloadDatabaseCredentialsAsync(Lifetime.Token);
        var settings = await ApiClient.GetDatabaseSettingsAsync(Lifetime.Token);
        OpenRouterSetting = settings.Single(Setting => Setting.Scope == "Api"
            && string.Equals(Setting.Key, "OpenRouter:ApiKey", StringComparison.OrdinalIgnoreCase));
        var overrideNotice = status.Overrides.Contains("OPENROUTER_API_KEY", StringComparer.OrdinalIgnoreCase)
            ? " The OPENROUTER_API_KEY deployment override remains effective." : "";
        Notice = $"OpenRouter key saved. {status.Status}.{overrideNotice}";
    }, "Couldn’t save OpenRouter");

    private async Task RunAsync(Func<Task> Action, string FailureTitle)
    {
        if (Busy) return;
        Busy = true;
        Error = Notice = null;
        try { await Action(); }
        catch (OperationCanceledException) when (Lifetime.IsCancellationRequested) { }
        catch (HttpRequestException Exception)
        {
            ErrorTitle = FailureTitle;
            Error = Exception.StatusCode switch
            {
                HttpStatusCode.Forbidden => "Deployment administrator access is required. Configure INTEGRATION_SETTINGS_ADMINISTRATORS on the API host.",
                HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "The API is running an older release that does not support provider setup yet. Deploy the current API revision, then retry.",
                HttpStatusCode.Conflict => "The OpenRouter setting changed in another session. Refresh before replacing it.",
                HttpStatusCode.BadRequest => "The OpenRouter key must be a nonempty printable token of at most 4096 characters.",
                HttpStatusCode.ServiceUnavailable => "The API could not access encrypted settings storage. Check the API logs and its database/encryption bootstrap, then retry.",
                _ => $"OpenRouter settings could not be saved (API returned {(int?)Exception.StatusCode ?? 0}). Retry after checking API status."
            };
        }
        catch (Exception) { ErrorTitle = FailureTitle; Error = "OpenRouter settings could not be loaded or saved. Retry shortly."; }
        finally { Busy = false; }
    }

    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync();
        Lifetime.Dispose();
    }
}
