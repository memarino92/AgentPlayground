using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PersonalAgent.Mobile.Services;

namespace PersonalAgent.Mobile.ViewModels;

public class MainViewModel(PersonalAgentApiClient apiClient, IPushTokenProvider pushTokenProvider, ILogger<MainViewModel> logger) : INotifyPropertyChanged
{
    private bool _isInitialized;
    private string _statusMessage = "Ready";
    private string _profileId = apiClient.ProfileId;
    private Guid? _lastApprovalId;
    private string? _lastApprovalStatus;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WebAppUrl => apiClient.WebAppUrl;
    public string ProfileId
    {
        get => _profileId;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.Equals(_profileId, normalized, StringComparison.Ordinal)) return;
            _profileId = normalized;
            OnPropertyChanged();
        }
    }
    public string? LastApprovalStatus
    {
        get => _lastApprovalStatus;
        private set
        {
            if (string.Equals(_lastApprovalStatus, value, StringComparison.Ordinal)) return;
            _lastApprovalStatus = value;
            OnPropertyChanged();
        }
    }
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

        pushTokenProvider.TokenUpdated += async (_, token) => await RegisterDeviceWithTokenAsync(token, cancellationToken);

        await RegisterDeviceAsync(cancellationToken);
        _isInitialized = true;
    }

    public async Task SaveProfileIdAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ProfileId))
        {
            StatusMessage = "Profile id is required";
            return;
        }

        apiClient.SetProfileId(ProfileId);
        StatusMessage = $"Profile set to {ProfileId}";
        await RegisterDeviceAsync(cancellationToken);
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

        await RegisterDeviceWithTokenAsync(pushToken, cancellationToken);
    }

    private async Task RegisterDeviceWithTokenAsync(string pushToken, CancellationToken cancellationToken)
    {
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
        _lastApprovalId = Guid.TryParse(approvalId, out var parsed) ? parsed : null;
        LastApprovalStatus = _lastApprovalId.HasValue ? "pending" : null;
        StatusMessage = approvalId is null ? "Failed to request 2FA" : $"2FA requested: {approvalId[..8]}...";
        return approvalId;
    }

    public async Task PollLastApprovalStatusAsync(CancellationToken cancellationToken = default)
    {
        if (_lastApprovalId is null)
        {
            StatusMessage = "No approval to refresh";
            return;
        }

        var status = await apiClient.GetApprovalStatusAsync(_lastApprovalId.Value, cancellationToken);
        if (string.IsNullOrWhiteSpace(status))
        {
            StatusMessage = "Approval status unavailable";
            return;
        }

        LastApprovalStatus = status;
        StatusMessage = $"Approval status: {status}";
    }

    public string GetApiBaseUrl() => apiClient.GetApiBaseUrl();

    private static string Short(string value) => value.Length <= 12 ? value : $"{value[..6]}...{value[^4..]}";

    public async Task SubmitApprovalDecisionAsync(Guid approvalId, bool approved, string reason, string decidedBy, CancellationToken cancellationToken = default)
    {
        await apiClient.SubmitApprovalDecisionAsync(approvalId, approved, reason, decidedBy, cancellationToken);
        _lastApprovalId = approvalId;
        await PollLastApprovalStatusAsync(cancellationToken);
    }

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
