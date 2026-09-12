using System.Diagnostics;
using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

namespace PersonalAgent.Tests.Services;

[Collection("Observability")]
public sealed class ObservabilityTests
{
    [Fact]
    public async Task UnreachableExporter_DoesNotFailApplicationWork()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddApplicationObservability("OutageTest");
        builder.Services.AddSingleton<IOtelSettingsStore>(new MemoryOtelStore
        {
            Revision = new(1, 1, new(OtelRuntimeTests.Values("http://127.0.0.1:1"))
            { ["Protocol"] = "http/protobuf", ["TimeoutMilliseconds"] = "100" })
        });
        using var host = builder.Build();
        await host.StartAsync();
        await host.Services.GetRequiredService<OtelRuntime>().ReloadAsync(default);
        var result = await AiTelemetry.RunAsync("synthetic", "CHAIN", () => Task.FromResult(42));
        result.Should().Be(42);
        host.Services.GetRequiredService<TracerProvider>().ForceFlush(1000);
        await host.StopAsync();
    }

    [Fact]
    public async Task AiFailure_PreservesParentAndKind_WithoutExceptionContent()
    {
        using var provider = Sdk.CreateTracerProviderBuilder().SetSampler(new AlwaysOnSampler()).AddSource(AiTelemetry.SourceName).Build();
        using var parent = new Activity("test").Start();
        Activity? child = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AiTelemetry.RunAsync<int>("test.model", "LLM", () =>
        {
            child = Activity.Current;
            throw new InvalidOperationException("private transcript");
        }, "test-model"));
        child.Should().NotBeNull();
        child!.TraceId.Should().Be(parent.TraceId);
        child.ParentSpanId.Should().Be(parent.SpanId);
        child.Status.Should().Be(ActivityStatusCode.Error);
        child.StatusDescription.Should().BeNull();
        child.GetTagItem("openinference.span.kind").Should().Be("LLM");
        child.Events.Should().BeEmpty();
        child.TagObjects.Should().NotContain(Tag => Equals(Tag.Value, "private transcript"));
    }

    [Fact]
    public void SpanPrivacy_RemovesSqlUrlsAndExceptionDetails()
    {
        using var activity = new Activity("query").Start();
        activity.SetTag("db.statement", "select 'private transcript'");
        activity.SetTag("url.full", "https://example.com/private?token=secret");
        activity.SetTag("http.request.method", "GET");
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.message"] = "private transcript", ["exception.stacktrace"] = "secret path"
        }));
        activity.SetStatus(ActivityStatusCode.Error, "private transcript");
        new TelemetryPrivacyProcessor().OnEnd(activity);
        activity.GetTagItem("db.statement").Should().BeNull();
        activity.GetTagItem("url.full").Should().BeNull();
        activity.GetTagItem("http.request.method").Should().Be("GET");
        activity.Events.Single().Tags.Should().BeEmpty();
        activity.StatusDescription.Should().BeNull();
    }

    [Fact]
    public void Logs_AreCorrelatedAndRedacted_BeforeExport()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddApplicationObservability("Test");
        var captured = new CaptureLogs();
        builder.Services.AddOpenTelemetry().WithLogging(Logs => Logs.AddProcessor(captured));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Test.Category");
        using var activity = new Activity("test").Start();
        using var scope = logger.BeginScope("secret scope");
        logger.LogError(new InvalidOperationException("secret exception"), "Private {Prompt}", "secret prompt");
        captured.Body.Should().Be("Application log (content redacted)");
        captured.Exception.Should().BeNull();
        captured.Attributes.Should().OnlyContain(Tag => Tag.Key == "error.type");
        captured.TraceId.Should().Be(activity.TraceId);
    }

    [Fact]
    public void SentryEvent_LinksToExistingTrace()
    {
        using var activity = new Activity("test").Start();
        var result = SentryErrorClient.CreateEvent(new("test", 1, "InvalidOperationException",
            activity.TraceId.ToString(), SpanId: activity.SpanId.ToString()), "Api");
        result.Contexts.Trace.TraceId.ToString().Should().Be(activity.TraceId.ToString());
        result.Contexts.Trace.SpanId.ToString().Should().Be(activity.SpanId.ToString());
    }

    [Fact]
    public void MissingTokenUsage_IsNotReportedAsZero()
    {
        using var activity = new Activity("test").Start();
        AiTelemetry.SetUsage(activity, null, 0, null);
        activity.GetTagItem("llm.token_count.prompt").Should().BeNull();
        activity.GetTagItem("llm.token_count.completion").Should().Be(0L);
        activity.GetTagItem("llm.token_count.total").Should().BeNull();
    }

    private sealed class CaptureLogs : BaseProcessor<LogRecord>
    {
        public string? Body { get; private set; }
        public Exception? Exception { get; private set; }
        public KeyValuePair<string, object?>[] Attributes { get; private set; } = [];
        public ActivityTraceId TraceId { get; private set; }
        public override void OnEnd(LogRecord Record)
        {
            if (Record.CategoryName != "Test.Category") return;
            Body = Record.Body;
            Exception = Record.Exception;
            Attributes = Record.Attributes?.ToArray() ?? [];
            TraceId = Record.TraceId;
        }
    }
}

[CollectionDefinition("Observability", DisableParallelization = true)]
public sealed class ObservabilityCollection;
