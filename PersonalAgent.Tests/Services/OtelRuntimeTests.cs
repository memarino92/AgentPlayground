using System.Diagnostics;
using AgentPlayground.Integrations;
using FluentAssertions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace PersonalAgent.Tests.Services;

[Collection("Observability")]
public sealed class OtelRuntimeTests
{
    [Fact]
    public async Task Reload_SwapsAllSignals_Disables_Reenables_AndRetainsWorkingRevisionOnFailure()
    {
        var store = new MemoryOtelStore { Revision = new(1, 1, Values()) };
        var factory = new FakeFactory();
        using var runtime = new OtelRuntime(store, factory, new("Api"));
        await runtime.ReloadAsync(default);
        await runtime.ReloadAsync(default);
        factory.Bundles.Should().HaveCount(1);
        runtime.Export(default(Batch<Activity>)).Should().Be(ExportResult.Success);
        runtime.Export(default(Batch<Metric>)).Should().Be(ExportResult.Success);
        runtime.Export(default(Batch<LogRecord>)).Should().Be(ExportResult.Success);
        factory.Exports.Should().Be(3);

        store.Revision = new(2, 2, Values("https://second.example", "0"));
        await runtime.ReloadAsync(default);
        factory.Disposals.Should().Be(3);
        runtime.Sample(new(default, ActivityTraceId.CreateRandom(), "root", ActivityKind.Internal)).Decision.Should().Be(SamplingDecision.Drop);
        store.Revision = new(3, 3, Values("https://third.example"));
        factory.Fail = true;
        await runtime.ReloadAsync(default);
        store.Last!.Revision.Should().Be(2);
        store.Last.Status.Should().StartWith("Apply failed");
        runtime.Export(default(Batch<Activity>));
        factory.Exports.Should().Be(4);

        store.Revision = new(4, 4, new(Values()) { ["Enabled"] = "false" });
        await runtime.ReloadAsync(default);
        store.Last!.Status.Should().StartWith("Disabled");
        runtime.Export(default(Batch<Activity>));
        factory.Exports.Should().Be(4);
        factory.Disposals.Should().Be(6);
        factory.Fail = false;
        store.Revision = new(5, 5, Values());
        await runtime.ReloadAsync(default);
        factory.Bundles.Should().HaveCount(3);
        store.Last!.Overrides.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Endpoint", "file:///secret")]
    [InlineData("Endpoint", "https://user:password@example.com")]
    [InlineData("Endpoint", "https://example.com?secret=value")]
    [InlineData("SampleRate", "NaN")]
    [InlineData("TimeoutMilliseconds", "0")]
    [InlineData("Headers", "authorization=secret\r\nx-other=bad")]
    [InlineData("Protocol", "json")]
    public void InvalidSettings_AreRejectedWithoutEchoingValues(string Key, string Value)
    {
        var values = Values();
        values[Key] = Value;
        var exception = Assert.Throws<IntegrationValidationException>(() => OtelSettings.Validate(values));
        exception.Errors.Should().ContainKey(Key);
        exception.Message.Should().NotContain(Value);
    }

    public static Dictionary<string, string> Values(string Endpoint = "https://collector.example", string Rate = "1")
        => new(OtelSettings.Defaults()) { ["Enabled"] = "true", ["Endpoint"] = Endpoint, ["SampleRate"] = Rate };

    internal sealed class FakeFactory : IOtelExporterFactory
    {
        public bool Fail;
        public int Exports;
        public int Disposals;
        public List<OtelExporters> Bundles { get; } = [];
        public OtelExporters Create(IReadOnlyDictionary<string, string> Values)
        {
            if (Fail) throw new InvalidOperationException("private header");
            var bundle = new OtelExporters(new Exporter<Activity>(this), new Exporter<Metric>(this), new Exporter<LogRecord>(this));
            Bundles.Add(bundle);
            return bundle;
        }
        private sealed class Exporter<T>(FakeFactory Factory) : BaseExporter<T> where T : class
        {
            public override ExportResult Export(in Batch<T> Batch) { Factory.Exports++; return ExportResult.Success; }
            protected override void Dispose(bool Disposing) { if (Disposing) Factory.Disposals++; }
        }
    }
}

internal sealed class MemoryOtelStore : IOtelSettingsStore
{
    public IntegrationRevision Revision = new(0, 0, OtelSettings.Defaults());
    public IntegrationInstanceResponse? Last;
    public Task<IntegrationRevision> ReadAsync(bool Active, CancellationToken CancellationToken) => Task.FromResult(Revision);
    public Task AcknowledgeAsync(IntegrationInstanceResponse Instance, CancellationToken CancellationToken) { Last = Instance; return Task.CompletedTask; }
    public Task<IReadOnlyList<IntegrationInstanceResponse>> InstancesAsync(CancellationToken CancellationToken) => Task.FromResult<IReadOnlyList<IntegrationInstanceResponse>>(Last is null ? [] : [Last]);
    public Task<long> SaveAsync(SaveIntegrationRequest Request, string Actor, CancellationToken CancellationToken) => throw new NotSupportedException();
    public Task ApplyAsync(long Revision, string Actor, CancellationToken CancellationToken) => throw new NotSupportedException();
}
