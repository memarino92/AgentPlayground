using OpenAI.Models;

namespace PersonalAgent.Services;

internal sealed class OpenAiChatModelDiscovery(Func<OpenAIModelClient> ClientFactory) : IChatModelDiscovery
{
    public OpenAiChatModelDiscovery(OpenAIModelClient Client) : this(() => Client) { }
    public async Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken CancellationToken = default)
    {
        var result = await ClientFactory().GetModelsAsync(CancellationToken);
        return result.Value.Select(Model => Model.Id).ToArray();
    }
}
