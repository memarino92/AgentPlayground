using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAgent.Web.Services;

internal class PersonalAgentClient(HttpClient httpClient)
{
    private const int DefaultPageSize = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SessionResponse?> CreateSessionAsync(string profileId, string? modelId = null)
    {
        var response = await httpClient.PostAsJsonAsync("/api/sessions", new { profileId, modelId });
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("create session", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SessionResponse>(content, JsonOptions);
    }

    public async Task<ModelCatalogResponse?> GetModelsAsync()
    {
        var response = await httpClient.GetAsync("/api/models");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load models", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<ModelCatalogResponse>(content, JsonOptions);
    }

    public async Task<MessageResponse?> SendMessageAsync(string sessionId, string profileId, string message, string? modelId = null)
    {
        var request = new { profileId, message, modelId };
        var response = await httpClient.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", request);
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("send message", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<MessageResponse>(content, JsonOptions);
    }

    public async Task<HistoryResponse?> GetHistoryAsync(string sessionId, string profileId)
    {
        var response = await httpClient.GetAsync($"/api/sessions/{sessionId}/messages?profileId={Uri.EscapeDataString(profileId)}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load chat history", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<HistoryResponse>(content, JsonOptions);
    }

    public async Task<SessionPageResponse?> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt = null, Guid? beforeSessionId = null, int pageSize = DefaultPageSize)
    {
        var query = new List<string>
        {
            $"profileId={Uri.EscapeDataString(profileId)}",
            $"pageSize={pageSize}"
        };

        if (beforeActivityAt is not null)
            query.Add($"beforeActivityAt={Uri.EscapeDataString(beforeActivityAt.Value.ToString("O"))}");

        if (beforeSessionId is not null)
            query.Add($"beforeSessionId={beforeSessionId}");

        var response = await httpClient.GetAsync($"/api/sessions?{string.Join("&", query)}");
        if (!response.IsSuccessStatusCode)
            throw await CreateRequestExceptionAsync("load sessions", response);

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SessionPageResponse>(content, JsonOptions);
    }

    private static async Task<PersonalAgentApiException> CreateRequestExceptionAsync(string operation, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return new PersonalAgentApiException(operation, response.StatusCode, response.ReasonPhrase, body);
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
public record MessageResponse(string SessionId, string ModelId, string Response);
public record HistoryResponse(string SessionId, string ModelId, List<ConversationMessage> Messages);
public record SessionPageResponse(List<SessionListItem> Sessions, DateTimeOffset? NextBeforeActivityAt, Guid? NextBeforeSessionId, bool HasMore);
public record SessionListItem(string SessionId, string Snippet, DateTimeOffset LastActivityAt, DateTimeOffset CreatedAt);
public record ConversationMessage(string Role, string Content);
public record AvailableChatModelResponse(string Id, string DisplayName, bool IsDefault);
