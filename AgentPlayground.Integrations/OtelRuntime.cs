using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;

namespace AgentPlayground.Integrations;

public interface IOtelExporterFactory
{
    OtelExporters Create(IReadOnlyDictionary<string, string> Values);
}

public sealed class OtelExporters(BaseExporter<Activity> Traces, BaseExporter<Metric> Metrics, BaseExporter<LogRecord> Logs) : IDisposable
{
    public IDisposable[] Owners { get; init; } = [];
    public BaseExporter<Activity> Traces { get; } = Traces;
    public BaseExporter<Metric> Metrics { get; } = Metrics;
    public BaseExporter<LogRecord> Logs { get; } = Logs;
    public void Dispose()
    {
        foreach (var exporter in Owners.Length > 0 ? Owners : new IDisposable[] { Traces, Metrics, Logs })
            try { exporter.Dispose(); } catch { /* Disposal cannot invalidate the replacement. */ }
    }
}

public sealed class OtelExporterFactory(IntegrationHost Host, IHostEnvironment Environment) : IOtelExporterFactory
{
    private readonly Resource Resource = ResourceBuilder.CreateDefault().AddService($"PersonalAgent.{Host.Service}",
        serviceVersion: System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion)
        .AddAttributes([new("deployment.environment.name", Environment.EnvironmentName)]).Build();

    public OtelExporters Create(IReadOnlyDictionary<string, string> Values)
    {
        OtelSettings.Validate(Values);
        OtlpExporterOptions Options(string Signal)
        {
            var http = Values["Protocol"] == "http/protobuf";
            var endpoint = Values["Endpoint"].TrimEnd('/') + "/";
            return new()
            {
                Endpoint = new Uri(http ? endpoint + "v1/" + Signal : endpoint),
                Protocol = http ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc,
                Headers = Values["Headers"],
                TimeoutMilliseconds = int.Parse(Values["TimeoutMilliseconds"], CultureInfo.InvariantCulture)
            };
        }
        BaseExporter<Activity> traces = bool.Parse(Values["TracesEnabled"]) ? new OtlpTraceExporter(Options("traces")) : new DisabledOtelExporter<Activity>();
        BaseExporter<Metric>? metrics = null;
        BaseExporter<LogRecord>? logs = null;
        var owners = new List<IDisposable>();
        try
        {
            metrics = bool.Parse(Values["MetricsEnabled"]) ? new OtlpMetricExporter(Options("metrics")) : new DisabledOtelExporter<Metric>();
            logs = bool.Parse(Values["LogsEnabled"]) ? new OtlpLogExporter(Options("logs")) : new DisabledOtelExporter<LogRecord>();
            ResourceBuilder ResourceBuilder() => OpenTelemetry.Resources.ResourceBuilder.CreateEmpty().AddAttributes(Resource.Attributes);
            // Exporter-only providers attach the resource and own shutdown. No sources, meters or instrumentation
            // are registered here; all collection stays in the single stable host pipeline.
            owners.Add(Sdk.CreateTracerProviderBuilder().SetResourceBuilder(ResourceBuilder())
                .AddProcessor(new SimpleActivityExportProcessor(traces)).Build());
            owners.Add(Sdk.CreateMeterProviderBuilder().SetResourceBuilder(ResourceBuilder())
                .AddReader(new PeriodicExportingMetricReader(metrics, Timeout.Infinite)).Build());
            var loggerFactory = LoggerFactory.Create(Builder => Builder.AddOpenTelemetry(Options => Options
                .SetResourceBuilder(ResourceBuilder()).AddProcessor(new SimpleLogRecordExportProcessor(logs))));
            loggerFactory.CreateLogger("ExporterOwner");
            owners.Add(loggerFactory);
            return new(traces, metrics, logs) { Owners = [.. owners] };
        }
        catch
        {
            traces.Dispose();
            metrics?.Dispose();
            logs?.Dispose();
            foreach (var owner in owners) owner.Dispose();
            throw;
        }
    }
}

public sealed class DisabledOtelExporter<T> : BaseExporter<T> where T : class
{
    public override ExportResult Export(in Batch<T> Batch) => ExportResult.Success;
}

public sealed class OtelRuntime(IOtelSettingsStore Store, IOtelExporterFactory Factory, IntegrationHost Host) : IDisposable
{
    private readonly SemaphoreSlim ReloadGate = new(1, 1);
    private readonly object ExportGate = new();
    private readonly string Instance = Guid.NewGuid().ToString("N");
    private OtelExporters? Exporters;
    private Sampler Sampler = new ParentBasedSampler(new AlwaysOnSampler());
    private string? AppliedValues;
    private long AppliedRevision;
    private bool Disposed;

    public SamplingResult Sample(in SamplingParameters Parameters) => Volatile.Read(ref Sampler).ShouldSample(Parameters);

    public async Task ReloadAsync(CancellationToken CancellationToken)
    {
        await ReloadGate.WaitAsync(CancellationToken);
        try
        {
            var revision = await Store.ReadAsync(true, CancellationToken);
            var status = "Disabled";
            try
            {
                OtelSettings.Validate(revision.Values);
                var serialized = JsonSerializer.Serialize(revision.Values.OrderBy(Pair => Pair.Key));
                if (serialized != AppliedValues)
                {
                    var sampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(double.Parse(revision.Values["SampleRate"], CultureInfo.InvariantCulture)));
                    var replacement = bool.Parse(revision.Values["Enabled"]) ? Factory.Create(revision.Values) : null;
                    OtelExporters? previous;
                    lock (ExportGate)
                    {
                        if (Disposed) { replacement?.Dispose(); return; }
                        previous = Exporters;
                        Exporters = replacement;
                        Volatile.Write(ref Sampler, sampler);
                        AppliedValues = serialized;
                    }
                    previous?.Dispose();
                }
                AppliedRevision = revision.ActiveRevision;
                status = bool.Parse(revision.Values["Enabled"]) ? "Applied (database)" : "Disabled (database)";
            }
            catch (Exception Exception) when (Exception is not OperationCanceledException)
            {
                status = "Apply failed; previous configuration retained";
            }
            await Store.AcknowledgeAsync(new(Host.Service, Instance, AppliedRevision, status, [], DateTimeOffset.UtcNow), CancellationToken);
        }
        finally { ReloadGate.Release(); }
    }

    public ExportResult Export<T>(in Batch<T> Batch) where T : class
    {
        lock (ExportGate)
        {
            if (Disposed || Exporters is null) return ExportResult.Success;
            object? exporter = typeof(T) == typeof(Activity) ? Exporters.Traces : typeof(T) == typeof(Metric) ? Exporters.Metrics : Exporters.Logs;
            try { return ((BaseExporter<T>)exporter).Export(Batch); }
            catch { return ExportResult.Failure; }
        }
    }

    public void Dispose()
    {
        OtelExporters? previous;
        lock (ExportGate)
        {
            Disposed = true;
            previous = Exporters;
            Exporters = null;
        }
        previous?.Dispose();
    }
}

public sealed class ReloadingOtelExporter<T>(OtelRuntime Runtime) : BaseExporter<T> where T : class
{
    public override ExportResult Export(in Batch<T> Batch) => Runtime.Export(Batch);
}

public sealed class ReloadingOtelSampler(OtelRuntime Runtime) : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters SamplingParameters) => Runtime.Sample(SamplingParameters);
}

public sealed class OtelReloadWorker(IntegrationDatabase Database, OtelRuntime Runtime, ILogger<OtelReloadWorker> Logger) : BackgroundService
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
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch { Logger.LogWarning("OpenTelemetry settings reconciliation failed; retaining current configuration and retrying"); }
            await Task.Delay(TimeSpan.FromSeconds(15), StoppingToken);
        }
    }
}
