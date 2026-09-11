using AgentPlayground.Integrations;

namespace PersonalAgent.Services;

internal interface IIntegrationSettingsService
{
    Task<IntegrationSettingsResponse> GetAsync(CancellationToken CancellationToken);
    Task<IntegrationSettingsResponse> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken);
    Task<IntegrationSettingsResponse> ApplyAsync(long Revision, string Actor, CancellationToken CancellationToken);
    Task<IntegrationSettingsResponse> ReloadAsync(CancellationToken CancellationToken);
    Task<IntegrationTestResponse> TestAsync(long Revision, CancellationToken CancellationToken);
}

internal sealed class IntegrationSettingsService(IIntegrationSettingsStore Store, IntegrationRuntime Runtime) : IIntegrationSettingsService
{
    public async Task<IntegrationSettingsResponse> GetAsync(CancellationToken CancellationToken)
    {
        var revision = await Store.ReadAsync(false, CancellationToken);
        var secrets = IntegrationRegistry.Fields.Where(Field => Field.Secret).Select(Field => Field.Key).ToHashSet();
        return new("sentry", revision.SavedRevision, revision.ActiveRevision, IntegrationRegistry.Fields,
            revision.Values.Where(Pair => !secrets.Contains(Pair.Key)).ToDictionary(),
            [.. secrets.Where(Key => !string.IsNullOrEmpty(revision.Values.GetValueOrDefault(Key)))],
            await Store.InstancesAsync(CancellationToken));
    }

    public async Task<IntegrationSettingsResponse> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken)
    {
        if (Request.Values is null || Request.Values.Count > 20 || Request.Values.Any(Pair => Pair.Value?.Length > 2048))
            throw new IntegrationValidationException(new() { ["Values"] = ["Provide supported settings with values no longer than 2048 characters."] });
        await Store.SaveAsync(Request, Actor, CancellationToken);
        return await GetAsync(CancellationToken);
    }

    public async Task<IntegrationSettingsResponse> ApplyAsync(long Revision, string Actor, CancellationToken CancellationToken)
    {
        await Store.ApplyAsync(Revision, Actor, CancellationToken);
        return await ReloadAsync(CancellationToken);
    }

    public async Task<IntegrationSettingsResponse> ReloadAsync(CancellationToken CancellationToken)
    {
        await Runtime.ReloadAsync(CancellationToken);
        return await GetAsync(CancellationToken);
    }

    public async Task<IntegrationTestResponse> TestAsync(long Revision, CancellationToken CancellationToken)
    {
        await Runtime.ReloadAsync(CancellationToken);
        var id = Runtime.CaptureTest(Revision);
        return new(id ?? "", id is null ? "Not queued: reporting is disabled, sampled out, or unavailable." : "Queued by API. Confirm receipt in Sentry; queueing does not verify ingestion.");
    }
}
