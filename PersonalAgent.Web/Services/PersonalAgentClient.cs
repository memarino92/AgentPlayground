using System.Net;
using AgentPlayground.Integrations;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using PersonalAgent.Web.Configuration;

namespace PersonalAgent.Web.Services;

internal class PersonalAgentClient(
    HttpClient httpClient,
    AuthenticationStateProvider authenticationStateProvider,
    IOptions<PersonalAgentApiOptions> options)
{
    private const int DefaultPageSize = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SessionResponse?> CreateSessionAsync(string profileId, string? modelId = null, string? actorId = null, string role = "Owner")
    {
        var response = await SendJsonAsync(HttpMethod.Post, "/api/sessions", new { profileId, modelId, actorId, role });
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("create session", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SessionResponse>(content, JsonOptions);
    }

    public async Task<ModelCatalogResponse?> GetModelsAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/models");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load models", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<ModelCatalogResponse>(content, JsonOptions);
    }

    public async Task<MessageResponse?> SendMessageAsync(string sessionId, string profileId, string message, string? actorId = null, string role = "Owner")
    {
        var request = new { profileId, message, actorId, role };
        var response = await SendJsonAsync(HttpMethod.Post, $"/api/sessions/{sessionId}/messages", request);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("send message", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<MessageResponse>(content, JsonOptions);
    }

    public async Task<HistoryResponse?> GetHistoryAsync(string sessionId, string profileId, string? actorId = null, string role = "Owner")
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/sessions/{sessionId}/messages?profileId={Uri.EscapeDataString(profileId)}&actorId={Uri.EscapeDataString(actorId ?? profileId)}&role={Uri.EscapeDataString(role)}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load chat history", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<HistoryResponse>(content, JsonOptions);
    }

    public async Task<SessionPageResponse?> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt = null, Guid? beforeSessionId = null, int pageSize = DefaultPageSize, string? actorId = null, string role = "Owner")
    {
        var query = new List<string>
        {
            $"profileId={Uri.EscapeDataString(profileId)}",
            $"actorId={Uri.EscapeDataString(actorId ?? profileId)}",
            $"role={Uri.EscapeDataString(role)}",
            $"pageSize={pageSize}"
        };

        if (beforeActivityAt is not null)
            query.Add($"beforeActivityAt={Uri.EscapeDataString(beforeActivityAt.Value.ToString("O"))}");

        if (beforeSessionId is not null)
            query.Add($"beforeSessionId={beforeSessionId}");

        var response = await SendAsync(HttpMethod.Get, $"/api/sessions?{string.Join("&", query)}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load sessions", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SessionPageResponse>(content, JsonOptions);
    }

    public async Task<CoachCheckinUploadResponse?> UploadCoachCheckinAsync(string profileId, string fileName, string contentType, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(profileId), "profileId");
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "audio/m4a" : contentType);
        form.Add(fileContent, "file", fileName);

        var response = await SendAsync(HttpMethod.Post, "/api/coach-checkins/uploads", form);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("upload coach check-in", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CoachCheckinUploadResponse>(content, JsonOptions);
    }

    public async Task<CoachCheckinStatusResponse?> GetCoachCheckinStatusAsync(Guid uploadId, string profileId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/coach-checkins/{uploadId}?profileId={Uri.EscapeDataString(profileId)}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load coach check-in status", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CoachCheckinStatusResponse>(content, JsonOptions);
    }

    public async Task<CoachCheckinSummaryResponse?> GetCoachCheckinSummaryAsync(Guid uploadId, string profileId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/coach-checkins/{uploadId}/summary?profileId={Uri.EscapeDataString(profileId)}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load coach check-in summary", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CoachCheckinSummaryResponse>(content, JsonOptions);
    }

    public async Task<CoachCheckinTranscriptResponse?> GetCoachCheckinTranscriptAsync(Guid uploadId, string? profileId = null)
    {
        var query = string.IsNullOrWhiteSpace(profileId) ? string.Empty : $"?profileId={Uri.EscapeDataString(profileId)}";
        var response = await SendAsync(HttpMethod.Get, $"/api/coach-checkins/{uploadId}/transcript{query}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load coach check-in transcript", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CoachCheckinTranscriptResponse>(content, JsonOptions);
    }

    public async Task<TranscriptDownloadResponse> DownloadCoachCheckinTranscriptAsync(Guid uploadId, string? profileId = null)
    {
        var query = string.IsNullOrWhiteSpace(profileId) ? string.Empty : $"?profileId={Uri.EscapeDataString(profileId)}";
        var response = await SendAsync(HttpMethod.Get, $"/api/coach-checkins/{uploadId}/transcript.txt{query}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("download coach check-in transcript", response);

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? $"coach-checkin-{uploadId}.txt";
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return new TranscriptDownloadResponse(fileName.Trim('"'), bytes);
    }

    public async Task ApplySpeakerOverridesAsync(Guid uploadId, string profileId, IReadOnlyList<SpeakerOverrideItem> overrides)
    {
        var response = await SendJsonAsync(HttpMethod.Post, $"/api/coach-checkins/{uploadId}/speaker-overrides", new
        {
            profileId,
            overrides
        });
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("apply speaker overrides", response);
    }

    public async Task<List<CoachCheckinAdminItemResponse>> GetCoachCheckinAdminItemsAsync(int limit = 100)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/coach-checkins/admin?limit={limit}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load coach check-in admin items", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<List<CoachCheckinAdminItemResponse>>(content, JsonOptions) ?? [];
    }

    public async Task<List<CoachCheckinAdminItemResponse>> GetCoachCheckinItemsAsync(string profileId, int limit = 100)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/coach-checkins?profileId={Uri.EscapeDataString(profileId)}&limit={limit}");
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync("load coach check-ins", response);
        return await response.Content.ReadFromJsonAsync<List<CoachCheckinAdminItemResponse>>(JsonOptions) ?? [];
    }

    public async Task<ToolAccessCatalogResponse> GetToolAccessAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/admin/tool-access");
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync("load tool access", response);
        return await response.Content.ReadFromJsonAsync<ToolAccessCatalogResponse>(JsonOptions)
            ?? new ToolAccessCatalogResponse([], []);
    }

    public async Task SaveToolAccessAsync(IReadOnlyList<ToolRolePermissionResponse> permissions, string updatedBy)
    {
        var response = await SendJsonAsync(HttpMethod.Put, "/api/admin/tool-access", new { permissions, updatedBy });
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync("save tool access", response);
    }

    public async Task<IReadOnlyList<string>> GetAssignedProfilesAsync(string actorId, string email)
    {
        var response = await SendAsync(HttpMethod.Get, "/api/coach-assignments");
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync("load coach assignments", response);
        return (await response.Content.ReadFromJsonAsync<AssignedProfilesResponse>(JsonOptions))?.Profiles ?? [];
    }

    public async Task<List<CoachProfileAssignmentResponse>> GetCoachAssignmentsAsync(string profileId)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/admin/coach-assignments?profileId={Uri.EscapeDataString(profileId)}");
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync("load coach assignments", response);
        return await response.Content.ReadFromJsonAsync<List<CoachProfileAssignmentResponse>>(JsonOptions) ?? [];
    }

    public async Task SaveCoachAssignmentAsync(string coachEmail, string subjectProfileId, bool isActive, string updatedBy)
    {
        var response = await SendJsonAsync(HttpMethod.Put, "/api/admin/coach-assignments", new { coachEmail, subjectProfileId, isActive, updatedBy });
        if (!response.IsSuccessStatusCode) throw await CreateRequestExceptionAsync("save coach assignment", response);
    }

    private static async Task<PersonalAgentApiException> CreateRequestExceptionAsync(string operation, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return new PersonalAgentApiException(operation, response.StatusCode, response.ReasonPhrase, body);
    }

    private Task<HttpResponseMessage> SendJsonAsync<T>(HttpMethod method, string uri, T value) =>
        SendAsync(method, uri, JsonContent.Create(value, options: JsonOptions));

    public Task<IntegrationSettingsResponse> GetIntegrationSettingsAsync(CancellationToken CancellationToken = default) =>
        IntegrationRequestAsync<IntegrationSettingsResponse>(HttpMethod.Get, "", null, CancellationToken);

    public Task<IntegrationSettingsResponse> SaveIntegrationSettingsAsync(SaveIntegrationRequest Request, CancellationToken CancellationToken = default) =>
        IntegrationRequestAsync<IntegrationSettingsResponse>(HttpMethod.Put, "", Request, CancellationToken);

    public Task<IntegrationSettingsResponse> ApplyIntegrationSettingsAsync(long Revision, CancellationToken CancellationToken = default) =>
        IntegrationRequestAsync<IntegrationSettingsResponse>(HttpMethod.Post, "/apply", new ApplyIntegrationRequest(Revision), CancellationToken);

    public Task<IntegrationSettingsResponse> ReloadIntegrationSettingsAsync(CancellationToken CancellationToken = default) =>
        IntegrationRequestAsync<IntegrationSettingsResponse>(HttpMethod.Post, "/reload", null, CancellationToken);

    public Task<IntegrationTestResponse> TestIntegrationSettingsAsync(long Revision, CancellationToken CancellationToken = default) =>
        IntegrationRequestAsync<IntegrationTestResponse>(HttpMethod.Post, "/test", new ApplyIntegrationRequest(Revision), CancellationToken);

    public async Task<List<DatabaseSetting>> GetDatabaseSettingsAsync(CancellationToken CancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/admin/settings", cancellationToken: CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<DatabaseSetting>>(JsonOptions, CancellationToken)
            ?? throw new InvalidOperationException("No settings response was returned.");
    }

    public async Task SaveDatabaseSettingsAsync(SaveDatabaseSettingsRequest Request, CancellationToken CancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Put, "/api/admin/settings", JsonContent.Create(Request, options: JsonOptions), CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<DatabaseCredentialStatus> ReloadDatabaseCredentialsAsync(CancellationToken CancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/admin/settings/reload", cancellationToken: CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DatabaseCredentialStatus>(JsonOptions, CancellationToken)
            ?? throw new InvalidOperationException("No credential status was returned.");
    }

    private async Task<T> IntegrationRequestAsync<T>(HttpMethod Method, string Suffix, object? Body, CancellationToken CancellationToken)
    {
        using var response = await SendAsync(Method, "/api/admin/integrations/sentry" + Suffix,
            Body is null ? null : JsonContent.Create(Body, options: JsonOptions), CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, CancellationToken)
            ?? throw new InvalidOperationException("No integration settings response was returned.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var role = user.IsInRole("Owner") ? "Owner" : user.IsInRole("Coach") ? "Coach" : null;
            var actorId = role switch
            {
                "Owner" => user.FindFirst("urn:github:login")?.Value,
                "Coach" => user.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id ? $"google:{id}" : null,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(role) && !string.IsNullOrWhiteSpace(actorId))
            {
                var email = user.FindFirst(ClaimTypes.Email)?.Value ?? string.Empty;
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                var payload = $"{actorId}\n{role}\n{email}\n{timestamp}";
                var signingKey = options.Value.ActorSigningKey;
                if (string.IsNullOrWhiteSpace(signingKey)) throw new InvalidOperationException("PersonalAgentApi:ActorSigningKey is required.");
                var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.UTF8.GetBytes(payload)));
                request.Headers.Add("X-Agent-Actor", actorId);
                request.Headers.Add("X-Agent-Role", role);
                request.Headers.Add("X-Agent-Email", email);
                request.Headers.Add("X-Agent-Timestamp", timestamp);
                request.Headers.Add("X-Agent-Signature", signature);
            }
        }
        return await httpClient.SendAsync(request, cancellationToken);
    }
}

internal class PersonalAgentApiException(string operation, HttpStatusCode statusCode, string? reasonPhrase, string responseBody)
    : HttpRequestException(
        $"Failed to {operation}. API returned {(int)statusCode} {reasonPhrase}{(string.IsNullOrWhiteSpace(responseBody) ? string.Empty : $": {responseBody}")}",
        inner: null,
        statusCode)
{
    public string Operation { get; } = operation;
    public string ResponseBody { get; } = responseBody;
}

public record SessionResponse(string SessionId, string ModelId, string Message);
public record ModelCatalogResponse(List<AvailableChatModelResponse> Models);
public record MessageResponse(string SessionId, string Response);
public record HistoryResponse(string SessionId, string ModelId, List<ConversationMessage> Messages);
public record SessionPageResponse(List<SessionListItem> Sessions, DateTimeOffset? NextBeforeActivityAt, Guid? NextBeforeSessionId, bool HasMore);
public record SessionListItem(string SessionId, string Snippet, DateTimeOffset LastActivityAt, DateTimeOffset CreatedAt);
public record ConversationMessage(string Role, string Content);
public record AvailableChatModelResponse(string Id, string DisplayName, bool IsDefault);
public record CoachCheckinUploadResponse(Guid UploadId, Guid CorrelationId, string Status, DateTimeOffset CreatedAtUtc, bool IsDuplicate);
public record CoachCheckinStatusResponse(Guid UploadId, Guid SessionId, string ProfileId, string Status, string? Error, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public record CoachCheckinSummaryResponse(Guid UploadId, Guid SessionId, string SummaryMarkdown, string SummaryJson, DateTimeOffset UpdatedAtUtc);
public record CoachCheckinTranscriptResponse(Guid UploadId, Guid SessionId, string ProfileId, string Status, string TranscriptText, DateTimeOffset UpdatedAtUtc, List<CoachCheckinTranscriptUtteranceResponse> Utterances);
public record CoachCheckinTranscriptUtteranceResponse(int SpeakerLabel, string SpeakerRole, int StartMs, int EndMs, string Text, double Confidence);
public record SpeakerOverrideItem(int SpeakerLabel, string Role);
public record CoachCheckinSpeakerLabelInfoResponse(int SpeakerLabel, string SpeakerRole, int UtteranceCount, List<string> SampleTexts);
public record CoachCheckinAdminItemResponse(Guid UploadId, Guid SessionId, string ProfileId, string OriginalFileName, string Status, string? Error, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, bool HasAudioBlob, int UtteranceCount, int ChunkCount, List<CoachCheckinSpeakerLabelInfoResponse> SpeakerLabels);
public record TranscriptDownloadResponse(string FileName, byte[] Bytes);
public record AgentToolDescriptorResponse(string Key, string Name, string DisplayName, string Integration, string Description, bool IsAvailable, bool OwnerDefault, bool CoachDefault);
public record ToolRolePermissionResponse(string Role, string ToolKey, bool IsEnabled);
public record ToolAccessCatalogResponse(List<AgentToolDescriptorResponse> Tools, List<ToolRolePermissionResponse> Permissions);
public record AssignedProfilesResponse(List<string> Profiles);
public record CoachProfileAssignmentResponse(string CoachEmail, string? CoachActorId, string SubjectProfileId, bool IsActive, DateTimeOffset UpdatedAt);
