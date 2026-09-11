using System.Text.Json;
using Microsoft.Extensions.Hosting;

namespace AgentPlayground.Integrations;

public sealed record IntegrationHost(string Service);

public sealed class IntegrationRuntime(IIntegrationSettingsStore Store, IIntegrationErrorClientFactory Factory, IntegrationHost Host) : IDisposable
{
    private readonly SemaphoreSlim ReloadGate = new(1, 1);
    private readonly object ClientGate = new();
    private readonly string Instance = Guid.NewGuid().ToString("N");
    private IIntegrationErrorClient? Client;
    private string? AppliedValues;
    private long AppliedRevision;
    private bool Disposed;

    public async Task ReloadAsync(CancellationToken CancellationToken)
    {
        await ReloadGate.WaitAsync(CancellationToken);
        try
        {
            var revision = await Store.ReadAsync(true, CancellationToken);
            var (values, overrides) = IntegrationRegistry.ResolveOverrides(revision.Values, Environment.GetEnvironmentVariable);
            var status = "Applied";
            try
            {
                IntegrationRegistry.Validate(values);
                var serialized = JsonSerializer.Serialize(values);
                if (serialized != AppliedValues)
                {
                    var replacement = bool.Parse(values["Enabled"]) ? Factory.Create(values, Host.Service) : null;
                    IIntegrationErrorClient? previous;
                    lock (ClientGate)
                    {
                        if (Disposed) { replacement?.Dispose(); return; }
                        previous = Client;
                        Client = replacement;
                        AppliedValues = serialized;
                    }
                    try { previous?.Dispose(); } catch { /* Replacement remains active if old transport shutdown fails. */ }
                }
                lock (ClientGate) AppliedRevision = revision.ActiveRevision;
                status = bool.Parse(values["Enabled"]) ? "Applied" : "Disabled";
            }
            catch (Exception Exception) when (Exception is not OperationCanceledException)
            {
                // Keep the last working client. Do not persist SDK exceptions that might contain a DSN.
                status = "Apply failed; previous configuration retained";
            }
            await Store.AcknowledgeAsync(new(Host.Service, Instance, AppliedRevision, status, overrides, DateTimeOffset.UtcNow), CancellationToken);
        }
        finally { ReloadGate.Release(); }
    }

    public string? Capture(IntegrationError Error)
    {
        lock (ClientGate)
        {
            if (Disposed || Client is null) return null;
            try { return Client.Capture(Error); }
            catch { return null; } // Telemetry must not interrupt application work.
        }
    }

    public string? CaptureTest(long Revision)
    {
        lock (ClientGate)
        {
            if (AppliedRevision != Revision) throw new IntegrationConflictException();
            return Capture(new("IntegrationTest", 0, null, null, true));
        }
    }

    public void Dispose()
    {
        lock (ClientGate)
        {
            if (Disposed) return;
            Disposed = true;
            try { Client?.Dispose(); } catch { /* Telemetry shutdown must not fail host shutdown. */ }
            Client = null;
        }
    }
}

public sealed class IntegrationReloadWorker(IntegrationDatabase Database, IntegrationRuntime Runtime,
    Microsoft.Extensions.Logging.ILogger<IntegrationReloadWorker> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        if (!Database.Available) return;
        var initialized = false;
        while (!StoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!initialized) { await Database.MigrateAsync(StoppingToken); initialized = true; }
                await Runtime.ReloadAsync(StoppingToken);
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { break; }
            catch
            {
                Microsoft.Extensions.Logging.LoggerExtensions.LogWarning(Logger, "Integration settings reconciliation failed; retaining current configuration and retrying");
            }
            await Task.Delay(TimeSpan.FromSeconds(15), StoppingToken);
        }
    }
}
