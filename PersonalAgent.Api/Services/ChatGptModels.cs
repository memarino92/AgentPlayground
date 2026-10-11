using System.Net.Http.Headers;
using System.Net.Http.Json;

using PersonalAgent.Api.Models;
using PersonalAgent.Integrations.ChatGpt;

namespace PersonalAgent.Api.Services;

internal static class ChatGptModelIds
{
    public static string Create(Guid Connection, string Slug) => $"chatgpt:{Connection}:{Slug}";
    public static bool TryParse(string Id, out Guid Connection, out string Slug)
    {
        var parts = Id.Split(':', 3);
        Slug = parts.Length == 3 ? parts[2] : "";
        Connection = default;
        return parts.Length == 3 && parts[0] == "chatgpt" && Guid.TryParse(parts[1], out Connection) && Slug.Length > 0;
    }
}

internal sealed class ChatGptModelCatalog(ChatGptConnectionStore Store,
    IHttpClientFactory Http, IChatModelCatalog Global, ILogger<ChatGptModelCatalog> Logger)
{
    public async Task<IReadOnlyList<AvailableChatModel>> GetModelsAsync(string ActorId, CancellationToken Token)
    {
        List<AvailableChatModel> result = [.. await Global.GetModelsAsync(Token)];
        var reviewed = await Store.ReadReviewedModelsAsync(Token);
        foreach (var account in await Store.ListAsync(ActorId, Token))
        {
            if (!account.PlanEnabled || !account.Connected) continue;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var discoveryToken = timeout.Token;
                var credentials = await Store.GetCredentialsAsync(ActorId, account.Id, discoveryToken);
                using var request = new HttpRequestMessage(HttpMethod.Get, ChatGptOAuth.Resource + "/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
                using var response = await Http.CreateClient("ChatGPT").SendAsync(request, discoveryToken);
                if (!response.IsSuccessStatusCode) continue;
                var catalog = await response.Content.ReadFromJsonAsync<Inventory>(discoveryToken);
                foreach (var model in catalog?.Models ?? [])
                    if (model.Visibility == "list" && reviewed.Contains(model.Slug))
                        result.Add(new(ChatGptModelIds.Create(account.Id, model.Slug), $"{model.Display_name} · ChatGPT · {account.Label}", false));
            }
            catch (Exception Exception) when (Exception is HttpRequestException or ChatGptException or OperationCanceledException or System.Text.Json.JsonException && !Token.IsCancellationRequested)
            {
                Logger.LogWarning("ChatGPT inventory unavailable for connection {ConnectionId}", account.Id);
            }
        }
        if (result.Count > 0 && !result.Any(Model => Model.IsDefault)) result[0] = result[0] with { IsDefault = true };
        return result;
    }

    private sealed record Inventory(List<Model> Models);
    private sealed record Model(string Slug, string Display_name, string Visibility);
}
