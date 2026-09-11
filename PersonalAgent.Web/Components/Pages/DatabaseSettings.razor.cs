using System.Net;
using AgentPlayground.Integrations;
using Microsoft.AspNetCore.Components;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Components.Pages;

public partial class DatabaseSettings : IAsyncDisposable
{
    [Inject] private PersonalAgentClient ApiClient { get; set; } = default!;
    private readonly CancellationTokenSource Lifetime = new();
    private List<SettingRow>? Rows;
    private bool Busy;
    private string Search = "";
    private string Scope = "";
    private string? Error;
    private string? Notice;
    private IEnumerable<SettingRow> VisibleRows => (Rows ?? []).Where(Row =>
        (Scope.Length == 0 || Row.Original.Scope == Scope) && Row.Original.Key.Contains(Search, StringComparison.OrdinalIgnoreCase));

    protected override Task OnInitializedAsync() => RefreshAsync();
    private Task RefreshAsync() => RunAsync(LoadAsync);
    private async Task LoadAsync() => Rows = (await ApiClient.GetDatabaseSettingsAsync(Lifetime.Token)).Select(Row => new SettingRow(Row)).ToList();
    private Task SaveAsync() => RunAsync(async () =>
    {
        var changes = Rows!.Where(Row => Row.Changed).Select(Row => new DatabaseSettingEdit(
            Row.Original.Scope, Row.Original.Key, Row.Original.Version,
            Row.Original.IsSecret ? Row.SecretAction switch { "keep" => null, "clear" => "", _ => Row.Value } : Row.Value,
            Row.IsActive)).ToList();
        if (changes.Count == 0) return;
        await ApiClient.SaveDatabaseSettingsAsync(new(changes), Lifetime.Token);
        // Discard entered credentials immediately, even if the subsequent refresh fails.
        Rows = null;
        Notice = "Saved to the database. Restart affected services to use these values; running configuration has not been verified.";
        await LoadAsync();
    });

    private static string ScopeName(string Scope) => Scope switch
    {
        "Shared" => "Shared (all services)", "Api" => "API service", "Web" => "Web application", "Worker" => "Background worker", _ => Scope
    };
    private static void ChangeSecretAction(SettingRow Row, ChangeEventArgs Event)
    {
        Row.SecretAction = Event.Value?.ToString() ?? "keep";
        Row.Value = "";
    }
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
                HttpStatusCode.Conflict => "A setting changed or was removed. Nothing in this batch was saved. Refresh and review before retrying.",
                HttpStatusCode.BadRequest => "Save rejected. Submit at most 500 changed settings with values no longer than 262144 characters.",
                _ => "Settings operation could not be confirmed. Refresh to check stored values before retrying."
            };
        }
        catch (Exception) { Error = "Settings operation could not be confirmed. Refresh to check stored values before retrying."; }
        finally { Busy = false; }
    }
    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync();
        Lifetime.Dispose();
    }
    private sealed class SettingRow(DatabaseSetting Setting)
    {
        public DatabaseSetting Original { get; } = Setting;
        public string Id { get; } = $"setting-{Guid.NewGuid():N}";
        public string Value { get; set; } = Setting.Value ?? "";
        public bool IsActive { get; set; } = Setting.IsActive;
        public string SecretAction { get; set; } = "keep";
        public bool Changed => IsActive != Original.IsActive || (Original.IsSecret ? SecretAction != "keep" : Value != Original.Value);
    }
}
