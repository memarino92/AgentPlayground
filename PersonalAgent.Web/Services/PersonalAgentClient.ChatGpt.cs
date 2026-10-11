using System.Net.Http.Json;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Web.Services;

internal partial class PersonalAgentClient
{
    public async Task DismissChatGptWelcomeAsync(Guid Id, CancellationToken Token)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/chatgpt/connections/{Id}/welcome", cancellationToken: Token);
        response.EnsureSuccessStatusCode();
    }
    public async Task<IReadOnlyList<ChatGptConnection>> GetChatGptConnectionsAsync(CancellationToken Token)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/chatgpt/connections", cancellationToken: Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<ChatGptConnection>>(Token) ?? [];
    }

    public async Task<ChatGptPairing> StartChatGptPairingAsync(Guid? Id, CancellationToken Token)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/chatgpt/pairings", JsonContent.Create(new ChatGptPairingRequest(Id)), Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatGptPairing>(Token))!;
    }

    public async Task<ChatGptDisconnect> DisconnectChatGptAsync(Guid Id, CancellationToken Token)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"/api/chatgpt/connections/{Id}", cancellationToken: Token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ChatGptDisconnect>(Token))!;
    }

    // No cookie identity is forwarded for the helper: the one-time pairing is the capability.
    public Task<HttpResponseMessage> RelayChatGptHandshakeAsync(ChatGptHandshake Handshake, CancellationToken Token)
        => httpClient.PostAsJsonAsync("/api/chatgpt/handshake", Handshake, Token);
}
