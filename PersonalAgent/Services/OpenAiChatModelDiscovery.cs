using OpenAI.Models;

namespace PersonalAgent.Services;

internal sealed class OpenAiChatModelDiscovery(OpenAIModelClient Client) : IChatModelDiscovery
{
    public async Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken CancellationToken = default)
    {
        var result = await Client.GetModelsAsync(CancellationToken);
        return result.Value.Select(Model => Model.Id).ToArray();
    }
}
