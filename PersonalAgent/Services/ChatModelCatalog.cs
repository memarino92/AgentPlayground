using Microsoft.Extensions.Options;
using System.Text.Json;

using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class ChatModelCatalog(
    IOptions<ChatModelCatalogOptions> Options,
    IChatModelDiscovery Discovery,
    TimeProvider TimeProvider,
    ILogger<ChatModelCatalog> Logger,
    IChatModelPolicySource? PolicySource = null) : IChatModelCatalog, IDisposable
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CatalogSnapshot? _snapshot;

    public async Task<IReadOnlyList<AvailableChatModel>> GetModelsAsync(CancellationToken CancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        await _refreshLock.WaitAsync(CancellationToken);
        try
        {
            var policy = PolicySource is null ? Options.Value : await PolicySource.ReadAsync(CancellationToken);
            var configuredModels = BuildModels(policy.Models);
            var policyKey = JsonSerializer.Serialize(policy);
            var snapshot = _snapshot?.PolicyKey == policyKey ? _snapshot : null;
            if (!policy.DiscoverFromProvider || configuredModels.Count is 0) return configuredModels;
            if (snapshot is not null && TimeProvider.GetUtcNow() < snapshot.RefreshAfter) return snapshot.Models;

            IReadOnlyList<AvailableChatModel> models;
            var refreshSeconds = policy.RefreshIntervalSeconds;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(policy.DiscoveryTimeoutSeconds));
            try
            {
                var ids = await Discovery.GetModelIdsAsync(timeout.Token).WaitAsync(timeout.Token);
                var availableIds = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
                models = NormalizeDefault(configuredModels.Where(Model => availableIds.Contains(Model.Id)).ToArray());
                Logger.LogInformation("Refreshed chat catalog: {ModelCount} configured models available", models.Count);
                if (models.Count is 0) Logger.LogWarning("Provider inventory contains no configured chat models");
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Provider exceptions may include response bodies. Log the type, not private provider details.
                models = snapshot?.Models ?? configuredModels;
                refreshSeconds = policy.FailureRetrySeconds;
                Logger.LogWarning("Chat model discovery failed ({ErrorType}); retaining {ModelCount} fallback models",
                    exception.GetType().Name, models.Count);
            }

            snapshot = new CatalogSnapshot(models, TimeProvider.GetUtcNow().AddSeconds(refreshSeconds), policyKey);
            Volatile.Write(ref _snapshot, snapshot);
            return snapshot.Models;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task<AvailableChatModel> GetDefaultModelAsync(CancellationToken CancellationToken = default) =>
        await FindModelAsync(null, CancellationToken)
            ?? throw new InvalidOperationException("No chat models are available.");

    public async Task<AvailableChatModel?> FindModelAsync(string? ModelId, CancellationToken CancellationToken = default)
    {
        var models = await GetModelsAsync(CancellationToken);
        return string.IsNullOrWhiteSpace(ModelId)
            ? models.FirstOrDefault(Model => Model.IsDefault)
            : models.FirstOrDefault(Model => string.Equals(Model.Id, ModelId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<AvailableChatModel> BuildModels(IEnumerable<ChatModelOption> ConfiguredModels)
    {
        var models = ConfiguredModels
            .Where(Model => !string.IsNullOrWhiteSpace(Model.Id))
            .Select(Model => new AvailableChatModel(Model.Id.Trim(),
                string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.Id.Trim() : Model.DisplayName.Trim(), Model.IsDefault))
            .DistinctBy(Model => Model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return NormalizeDefault(models);
    }

    private static IReadOnlyList<AvailableChatModel> NormalizeDefault(AvailableChatModel[] Models)
    {
        var defaultIndex = Array.FindIndex(Models, Model => Model.IsDefault);
        if (defaultIndex < 0) defaultIndex = 0;
        return Array.AsReadOnly(Models.Select((Model, Index) => Model with { IsDefault = Index == defaultIndex }).ToArray());
    }

    public void Dispose() => _refreshLock.Dispose();

    private sealed record CatalogSnapshot(IReadOnlyList<AvailableChatModel> Models, DateTimeOffset RefreshAfter, string PolicyKey);
}
