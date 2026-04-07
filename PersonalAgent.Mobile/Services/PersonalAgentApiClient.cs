using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalAgent.Mobile.Configuration;
using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public class PersonalAgentApiClient(HttpClient httpClient, IOptions<MobileAppOptions> options, ILogger<PersonalAgentApiClient> logger)
{
    private readonly MobileAppOptions _options = options.Value;
    private const string ProfileIdPreferenceKey = "ProfileId";

    public string WebAppUrl => _options.WebAppUrl;
    public string ProfileId => GetProfileId();

    public async Task<ApiCallResult> RegisterDeviceTokenAsync(string pushToken, CancellationToken cancellationToken = default)
    {
        var request = new RegisterMobileDeviceTokenRequest(
            GetProfileId(),
            GetDeviceId(),
            "android",
            pushToken,
            AppInfo.Current.VersionString);

        try
        {
            using var response = await httpClient.PostAsJsonAsync("api/mobile/devices/register", request, cancellationToken);
            if (response.IsSuccessStatusCode) return ApiCallResult.Success();

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = $"{(int)response.StatusCode} {response.StatusCode}: {error}";
            logger.LogWarning("Device token registration failed: {StatusCode} {Error}", response.StatusCode, error);
            return ApiCallResult.Failure(message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Device token registration failed with exception");
            return ApiCallResult.Failure(ex.Message);
        }
    }

    public async Task<string?> RequestTestApprovalAsync(CancellationToken cancellationToken = default)
    {
        var request = new RequestAgentApprovalRequest(
            GetProfileId(),
            $"mobile-session-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            "ToolCallGuard",
            "Approve test action from Android companion app",
            "mobile-debug",
            5);

        using var response = await httpClient.PostAsJsonAsync("api/approvals", request, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<ApprovalCreateResponse>(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Approval request failed: {StatusCode}", response.StatusCode);
            return null;
        }

        return payload?.ApprovalId;
    }

    public async Task SubmitApprovalDecisionAsync(Guid approvalId, bool approved, string reason, string decidedBy, CancellationToken cancellationToken = default)
    {
        var request = new CompleteAgentApprovalRequest(GetProfileId(), approved, decidedBy, reason);
        using var response = await httpClient.PostAsJsonAsync($"api/approvals/{approvalId}/decision", request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning("Approval decision failed: {StatusCode} {Error}", response.StatusCode, error);
    }

    public async Task<string?> GetApprovalStatusAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync($"api/approvals/{approvalId}", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var payload = await response.Content.ReadFromJsonAsync<ApprovalStatusResponse>(cancellationToken);
            return payload?.Status;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading approval status for {ApprovalId}", approvalId);
            return null;
        }
    }

    private static string GetDeviceId()
    {
        var existing = Preferences.Default.Get("DeviceId", string.Empty);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var generated = $"android-{Guid.NewGuid():N}";
        Preferences.Default.Set("DeviceId", generated);
        return generated;
    }

    public string GetApiBaseUrl() => httpClient.BaseAddress?.ToString() ?? _options.ApiBaseUrl;

    public void SetProfileId(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        Preferences.Default.Set(ProfileIdPreferenceKey, profileId.Trim());
    }

    private string GetProfileId()
    {
        var stored = Preferences.Default.Get(ProfileIdPreferenceKey, string.Empty);
        return string.IsNullOrWhiteSpace(stored) ? _options.ProfileId : stored;
    }

    public sealed record ApiCallResult(bool IsSuccess, string? Error)
    {
        public static ApiCallResult Success() => new(true, null);
        public static ApiCallResult Failure(string error) => new(false, error);
    }

    private sealed record ApprovalCreateResponse(string ApprovalId, string Status, DateTimeOffset ExpiresAt);
    private sealed record ApprovalStatusResponse(string ApprovalId, string Status);
}
