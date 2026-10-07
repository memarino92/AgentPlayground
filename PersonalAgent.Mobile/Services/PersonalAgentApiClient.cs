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
    private const string ApiBaseUrlPreferenceKey = "ApiBaseUrl";
    private const string WebAppUrlPreferenceKey = "WebAppUrl";
    private const string InternalApiKeyPreferenceKey = "InternalApiKey";

    public string WebAppUrl => GetWebAppUrl();
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
            using var message = CreateRequest(HttpMethod.Post, "api/mobile/devices/register", JsonContent.Create(request));
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode) return ApiCallResult.Success();

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            var errorMessage = $"{(int)response.StatusCode} {response.StatusCode}: {error}";
            logger.LogWarning("Device token registration failed: {StatusCode} {Error}", response.StatusCode, error);
            return ApiCallResult.Failure(errorMessage);
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

        using var message = CreateRequest(HttpMethod.Post, "api/approvals", JsonContent.Create(request));
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Approval request failed: {StatusCode}", response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<ApprovalCreateResponse>(cancellationToken);
        return payload?.ApprovalId;
    }

    public async Task<ApiCallResult> SubmitApprovalDecisionAsync(Guid approvalId, bool approved, string reason, string decidedBy, CancellationToken cancellationToken = default)
    {
        var request = new CompleteAgentApprovalRequest(GetProfileId(), approved, decidedBy, reason);
        try
        {
            using var message = CreateRequest(HttpMethod.Post, $"api/approvals/{approvalId}/decision", JsonContent.Create(request));
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode) return ApiCallResult.Success();
            logger.LogWarning("Approval decision failed: {StatusCode}", response.StatusCode);
            return ApiCallResult.Failure($"Decision was not accepted ({(int)response.StatusCode}). Refresh and try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Failed submitting approval {ApprovalId}", approvalId);
            return ApiCallResult.Failure("Could not confirm the decision. Check your connection and refresh before retrying.");
        }
    }

    public async Task<AgentApprovalDetails?> GetApprovalAsync(Guid ApprovalId, CancellationToken CancellationToken = default)
    {
        using var Message = CreateRequest(HttpMethod.Get, $"api/approvals/{ApprovalId}");
        using var Response = await httpClient.SendAsync(Message, CancellationToken);
        if (!Response.IsSuccessStatusCode) return null;
        var Approval = await Response.Content.ReadFromJsonAsync<AgentApprovalDetails>(CancellationToken);
        return Approval is not null && string.Equals(Approval.ProfileId, GetProfileId(), StringComparison.OrdinalIgnoreCase) ? Approval : null;
    }

    public async Task<string?> GetApprovalStatusAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var message = CreateRequest(HttpMethod.Get, $"api/approvals/{approvalId}");
            using var response = await httpClient.SendAsync(message, cancellationToken);
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

    public string GetApiBaseUrl() => GetEffectiveApiBaseUrl();

    public string GetWebAppUrl()
    {
        var stored = Preferences.Default.Get(WebAppUrlPreferenceKey, string.Empty);
        return string.IsNullOrWhiteSpace(stored) ? _options.WebAppUrl : stored;
    }

    public string GetInternalApiKey()
    {
        var stored = Preferences.Default.Get(InternalApiKeyPreferenceKey, string.Empty);
        return string.IsNullOrWhiteSpace(stored) ? _options.InternalApiKey : stored;
    }

    public void SetConnectionSettings(string apiBaseUrl, string webAppUrl, string internalApiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiBaseUrl)) Preferences.Default.Set(ApiBaseUrlPreferenceKey, apiBaseUrl.Trim());
        if (!string.IsNullOrWhiteSpace(webAppUrl)) Preferences.Default.Set(WebAppUrlPreferenceKey, webAppUrl.Trim());
        Preferences.Default.Set(InternalApiKeyPreferenceKey, internalApiKey?.Trim() ?? string.Empty);
    }

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

    private string GetEffectiveApiBaseUrl()
    {
        var stored = Preferences.Default.Get(ApiBaseUrlPreferenceKey, string.Empty);
        return string.IsNullOrWhiteSpace(stored) ? _options.ApiBaseUrl : stored;
    }

    private HttpRequestMessage CreateRequest(HttpMethod Method, string Path, HttpContent? Content = null)
    {
        var Message = new HttpRequestMessage(Method, new Uri(new Uri(EnsureTrailingSlash(GetEffectiveApiBaseUrl())), Path)) { Content = Content };
        var internalApiKey = GetInternalApiKey();
        if (!string.IsNullOrWhiteSpace(internalApiKey))
            Message.Headers.Add("X-Internal-Api-Key", internalApiKey);
        return Message;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : $"{value}/";

    public sealed record ApiCallResult(bool IsSuccess, string? Error)
    {
        public static ApiCallResult Success() => new(true, null);
        public static ApiCallResult Failure(string error) => new(false, error);
    }

    private sealed record ApprovalCreateResponse(string ApprovalId, string Status, DateTimeOffset ExpiresAt);
    private sealed record ApprovalStatusResponse(string ApprovalId, string Status);
}
