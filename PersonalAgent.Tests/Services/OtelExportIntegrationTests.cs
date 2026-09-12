using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace PersonalAgent.Tests.Services;

[Collection("Observability")]
public sealed class OtelExportIntegrationTests
{
    [Fact]
    public async Task RealOtlp_AllSignalsKeepServiceResource_AndSwitchDestinationsWithoutRestart()
    {
        var received = new ConcurrentQueue<(string Path, string Body)>();
        var collectorBuilder = WebApplication.CreateSlimBuilder();
        collectorBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        collectorBuilder.Logging.ClearProviders();
        await using var collector = collectorBuilder.Build();
        collector.MapPost("/{**path}", async (HttpContext Context) =>
        {
            using var buffer = new MemoryStream();
            await Context.Request.Body.CopyToAsync(buffer);
            received.Enqueue((Context.Request.Path.Value!, Encoding.UTF8.GetString(buffer.ToArray())));
            return Results.Bytes([], "application/x-protobuf");
        });
        await collector.StartAsync();
        var endpoint = collector.Urls.Single();
        var store = new MemoryOtelStore { Revision = new(1, 1, new(OtelRuntimeTests.Values(endpoint + "/first"))
        {
            ["Protocol"] = "http/protobuf", ["LogsEnabled"] = "true", ["MetricsEnabled"] = "true"
        }) };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddApplicationObservability("CollectorTest");
        builder.Services.AddSingleton(new IntegrationDatabase("", ""));
        builder.Services.AddSingleton<IOtelSettingsStore>(store);
        using var host = builder.Build();
        await host.StartAsync();
        var runtime = host.Services.GetRequiredService<OtelRuntime>();
        await runtime.ReloadAsync(default);
        var meter = host.Services.GetRequiredService<IMeterFactory>().Create(AiMetricsProcessor.MeterName);
        var counter = meter.CreateCounter<long>("test.export.counter");
        async Task Emit()
        {
            await AiTelemetry.RunAsync("export-check", "CHAIN", () => Task.FromResult(1));
            counter.Add(1);
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ExportTest").LogWarning("private-content");
            host.Services.GetRequiredService<TracerProvider>().ForceFlush(5000).Should().BeTrue();
            host.Services.GetRequiredService<MeterProvider>().ForceFlush(5000).Should().BeTrue();
            host.Services.GetRequiredService<LoggerProvider>().ForceFlush(5000).Should().BeTrue();
        }
        await Emit();
        foreach (var signal in new[] { "traces", "metrics", "logs" })
            received.Should().Contain(Item => Item.Path == "/first/v1/" + signal && Item.Body.Contains("PersonalAgent.CollectorTest"));
        received.Should().OnlyContain(Item => !Item.Body.Contains("private-content"));

        store.Revision = new(2, 2, new(store.Revision.Values) { ["Endpoint"] = endpoint + "/second", ["MetricsEnabled"] = "false", ["LogsEnabled"] = "false" });
        await runtime.ReloadAsync(default);
        received.Clear();
        await Emit();
        received.Should().NotBeEmpty().And.OnlyContain(Item => Item.Path == "/second/v1/traces");
        store.Revision = new(3, 3, new(store.Revision.Values) { ["Enabled"] = "false" });
        await runtime.ReloadAsync(default);
        received.Clear();
        await Emit();
        received.Should().BeEmpty();
        await host.StopAsync();
    }
}
