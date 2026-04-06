using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonalAgent.Mobile.Configuration;
using PersonalAgent.Mobile.Models;

namespace PersonalAgent.Mobile.Services;

public class PersonalAgentApiClient(HttpClient httpClient, IOptions<MobileAppOptions> options, ILogger<PersonalAgentApiClient> logger)
{
    private readonly MobileAppOptions _options = options.Value;

    public string WebAppUrl => _options.WebAppUrl;
    public string ProfileId => _options.ProfileId;

    public async Task RegisterDeviceTokenAsync(string pushToken, CancellationToken cancellationToken = default)
    {
        var request = new RegisterMobileDeviceTokenRequest(
            _options.ProfileId,
            GetDeviceId(),
            "android",
            pushToken,
            AppInfo.Current.VersionString);

        using var response = await httpClient.PostAsJsonAsync("api/mobile/devices/register", request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning("Device token registration failed: {StatusCode} {Error}", response.StatusCode, error);
    }

    public async Task<string?> RequestTestApprovalAsync(CancellationToken cancellationToken = default)
    {
        var request = new RequestAgentApprovalRequest(
            _options.ProfileId,
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
        var request = new CompleteAgentApprovalRequest(_options.ProfileId, approved, decidedBy, reason);
        using var response = await httpClient.PostAsJsonAsync($"api/approvals/{approvalId}/decision", request, cancellationToken);
        if (response.IsSuccessStatusCode) return;

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning("Approval decision failed: {StatusCode} {Error}", response.StatusCode, error);
    }

    private static string GetDeviceId()
    {
        var existing = Preferences.Default.Get("DeviceId", string.Empty);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var generated = $"android-{Guid.NewGuid():N}";
        Preferences.Default.Set("DeviceId", generated);
        return generated;
    }

    private sealed record ApprovalCreateResponse(string ApprovalId, string Status, DateTimeOffset ExpiresAt);
}
