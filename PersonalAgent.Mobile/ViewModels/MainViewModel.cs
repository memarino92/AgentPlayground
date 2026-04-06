using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PersonalAgent.Mobile.Services;

namespace PersonalAgent.Mobile.ViewModels;

public class MainViewModel(PersonalAgentApiClient apiClient, IPushTokenProvider pushTokenProvider, ILogger<MainViewModel> logger) : INotifyPropertyChanged
{
    private bool _isInitialized;
    private string _statusMessage = "Ready";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WebAppUrl => apiClient.WebAppUrl;
    public string ProfileId => apiClient.ProfileId;
    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (string.Equals(_statusMessage, value, StringComparison.Ordinal)) return;
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        await RegisterDeviceAsync(cancellationToken);
        _isInitialized = true;
        OnPropertyChanged(nameof(ProfileId));
    }

    public async Task RegisterDeviceAsync(CancellationToken cancellationToken = default)
    {
        var pushToken = await pushTokenProvider.GetPushTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(pushToken))
        {
            StatusMessage = "Push token unavailable";
            logger.LogWarning("Push token unavailable; device token registration skipped");
            return;
        }

        StatusMessage = "Registering device...";
        var result = await apiClient.RegisterDeviceTokenAsync(pushToken, cancellationToken);
        StatusMessage = result.IsSuccess
            ? $"Device token registered ({Short(pushToken)})"
            : $"Register failed: {result.Error}";
    }

    public async Task<string?> RequestTestApprovalAsync(CancellationToken cancellationToken = default)
    {
        StatusMessage = "Requesting 2FA...";
        var approvalId = await apiClient.RequestTestApprovalAsync(cancellationToken);
        StatusMessage = approvalId is null ? "Failed to request 2FA" : $"2FA requested: {approvalId[..8]}...";
        return approvalId;
    }

    public string GetApiBaseUrl() => apiClient.GetApiBaseUrl();

    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..6]}...{value[^4..]}";

    public Task SubmitApprovalDecisionAsync(Guid approvalId, bool approved, string reason, string decidedBy, CancellationToken cancellationToken = default) =>
        apiClient.SubmitApprovalDecisionAsync(approvalId, approved, reason, decidedBy, cancellationToken);

    public async Task TryInjectProfileIntoWebViewAsync(WebView webView)
    {
        if (string.IsNullOrWhiteSpace(ProfileId)) return;
        var js = $"window.localStorage.setItem('personalagent.profileId', '{ProfileId.Replace("'", "\\'")}');";
        try
        {
            _ = await webView.EvaluateJavaScriptAsync(js);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to inject profile id into WebView localStorage");
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
