using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalAgent.Web.Services;

internal class PersonalAgentClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SessionResponse?> CreateSessionAsync()
    {
        var response = await httpClient.PostAsJsonAsync("/api/sessions", new { });
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<SessionResponse>(content, JsonOptions);
    }

    public async Task<MessageResponse?> SendMessageAsync(string sessionId, string message)
    {
        var request = new { message };
        var response = await httpClient.PostAsJsonAsync($"/api/sessions/{sessionId}/messages", request);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<MessageResponse>(content, JsonOptions);
    }

    public async Task<HistoryResponse?> GetHistoryAsync(string sessionId)
    {
        var response = await httpClient.GetAsync($"/api/sessions/{sessionId}/messages");
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<HistoryResponse>(content, JsonOptions);
    }
}

internal record SessionResponse(string SessionId, string Message);
internal record MessageResponse(string SessionId, string Response);
internal record HistoryResponse(string SessionId, List<ConversationMessage> Messages);
internal record ConversationMessage(string Role, string Content);
