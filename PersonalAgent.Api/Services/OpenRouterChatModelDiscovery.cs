using OpenAI.Models;

namespace PersonalAgent.Api.Services;

internal sealed class OpenRouterChatModelDiscovery(Func<OpenAIModelClient> ClientFactory) : IChatModelDiscovery
{
    public OpenRouterChatModelDiscovery(OpenAIModelClient Client) : this(() => Client) { }

    public async Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken CancellationToken = default)
    {
        var result = await ClientFactory().GetModelsAsync(CancellationToken);
        return result.Value.Select(Model => OpenRouterModelIds.ToCatalogId(Model.Id)).ToArray();
    }
}
