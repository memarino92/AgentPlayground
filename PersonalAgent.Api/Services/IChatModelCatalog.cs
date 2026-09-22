using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal interface IChatModelCatalog
{
    Task<IReadOnlyList<AvailableChatModel>> GetModelsAsync(CancellationToken CancellationToken = default);
    Task<AvailableChatModel> GetDefaultModelAsync(CancellationToken CancellationToken = default);
    Task<AvailableChatModel?> FindModelAsync(string? ModelId, CancellationToken CancellationToken = default);
}
