namespace PersonalAgent.Api.Services;

internal sealed record ChatModelDiscoverySource(string Name, Func<bool> IsConfigured, IChatModelDiscovery Discovery);

internal sealed class CompositeChatModelDiscovery(
    IReadOnlyList<ChatModelDiscoverySource> Sources,
    ILogger<CompositeChatModelDiscovery> Logger) : IChatModelDiscovery
{
    public async Task<IReadOnlyList<string>> GetModelIdsAsync(CancellationToken CancellationToken = default)
    {
        var active = Sources.Where(Source => Source.IsConfigured()).ToArray();
        if (active.Length is 0) return [];
        var results = await Task.WhenAll(active.Select(Source => DiscoverAsync(Source, CancellationToken)));
        var successful = results.Where(Result => Result.Error is null).ToArray();
        foreach (var failed in results.Where(Result => Result.Error is not null))
            Logger.LogWarning("{Provider} chat model discovery failed ({ErrorType})", failed.Name, failed.Error!.GetType().Name);
        if (successful.Length is 0)
            throw new AggregateException(results.Select(Result => Result.Error!));
        return successful.SelectMany(Result => Result.Models).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<DiscoveryResult> DiscoverAsync(ChatModelDiscoverySource Source, CancellationToken CancellationToken)
    {
        try { return new(Source.Name, await Source.Discovery.GetModelIdsAsync(CancellationToken), null); }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception Exception) { return new(Source.Name, [], Exception); }
    }

    private sealed record DiscoveryResult(string Name, IReadOnlyList<string> Models, Exception? Error);
}
