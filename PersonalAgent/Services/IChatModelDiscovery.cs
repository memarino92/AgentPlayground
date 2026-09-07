namespace PersonalAgent.Services;

// Provider adapters return inventory only. The catalog owns application eligibility and defaults.
internal interface IChatModelDiscovery
{
    Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken CancellationToken = default);
}
