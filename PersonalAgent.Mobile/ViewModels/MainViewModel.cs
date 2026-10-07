using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using PersonalAgent.Mobile.Models;
using PersonalAgent.Mobile.Services;

namespace PersonalAgent.Mobile.ViewModels;

public class MainViewModel(PersonalAgentApiClient apiClient, IPushTokenProvider pushTokenProvider, ILogger<MainViewModel> logger) : INotifyPropertyChanged
{
    private bool _isInitialized;
    private string _statusMessage = "Ready";
    private string _profileId = apiClient.ProfileId;
    private string _apiBaseUrl = apiClient.GetApiBaseUrl();
    private string _webAppUrl = apiClient.GetWebAppUrl();
    private string _internalApiKey = apiClient.GetInternalApiKey();
    private Guid? _lastApprovalId;
    private string? _lastApprovalStatus;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string WebAppUrl
    {
        get => _webAppUrl;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.Equals(_webAppUrl, normalized, StringComparison.Ordinal)) return;
            _webAppUrl = normalized;
            OnPropertyChanged();
        }
    }

    public string ApiBaseUrl
    {
        get => _apiBaseUrl;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (string.Equals(_apiBaseUrl, normalized, StringComparison.Ordinal)) return;
            _apiBaseUrl = normalized;
            OnPropertyChanged();
        }
    }

    public string InternalApiKey
    {
        get => _internalApiKey;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_internalApiKey, normalized, StringComparison.Ordinal)) return;
            _internalApiKey = normalized;
            OnPropertyChanged();
        }
    }
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
        _isInitialized = true;

        pushTokenProvider.TokenUpdated += async (_, token) => await MainThread.InvokeOnMainThreadAsync(() => RegisterDeviceWithTokenAsync(token, CancellationToken.None));

        await RegisterDeviceAsync(cancellationToken);
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

    public async Task SaveConnectionSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ApiBaseUrl) || string.IsNullOrWhiteSpace(WebAppUrl))
        {
            StatusMessage = "API and Web URLs are required";
            return;
        }

        apiClient.SetConnectionSettings(ApiBaseUrl, WebAppUrl, InternalApiKey);
        StatusMessage = "Connection settings saved";
        await RegisterDeviceAsync(cancellationToken);
    }

    public async Task RegisterDeviceAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(InternalApiKey))
        {
            StatusMessage = "Push registration needs device connection settings";
            return;
        }
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
            ? "Device registered for push notifications"
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

    public Task<AgentApprovalDetails?> GetApprovalAsync(Guid ApprovalId) => apiClient.GetApprovalAsync(ApprovalId);

    public async Task<PersonalAgentApiClient.ApiCallResult> SubmitApprovalDecisionAsync(Guid approvalId, bool approved, string reason, string decidedBy, CancellationToken cancellationToken = default)
    {
        var result = await apiClient.SubmitApprovalDecisionAsync(approvalId, approved, reason, decidedBy, cancellationToken);
        if (!result.IsSuccess)
        {
            StatusMessage = result.Error ?? "Decision failed";
            return result;
        }
        _lastApprovalId = approvalId;
        await PollLastApprovalStatusAsync(cancellationToken);
        return result;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
