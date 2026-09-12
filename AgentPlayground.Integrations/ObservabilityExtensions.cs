using System.Diagnostics;
using System.Reflection;
using MassTransit.Logging;
using MassTransit.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AgentPlayground.Integrations;

public static class ObservabilityExtensions
{
    public static IHostApplicationBuilder AddApplicationObservability(this IHostApplicationBuilder Builder, string Service)
    {
        Builder.Services.TryAddSingleton(IntegrationDatabase.FromConfiguration(Builder.Configuration));
        Builder.Services.TryAddSingleton(new IntegrationHost(Service));
        Builder.Services.TryAddSingleton<IOtelSettingsStore, OtelSettingsStore>();
        Builder.Services.TryAddSingleton<IOtelExporterFactory, OtelExporterFactory>();
        Builder.Services.TryAddSingleton<OtelRuntime>();
        Builder.Services.AddHostedService<OtelReloadWorker>();
        Builder.Services.Configure<OpenTelemetryLoggerOptions>(Options =>
        {
            Options.IncludeScopes = false;
            Options.IncludeFormattedMessage = false;
        });
        Builder.Services.AddOpenTelemetry()
            .ConfigureResource(Resource => Resource.AddService($"PersonalAgent.{Service}",
                serviceVersion: Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
                .AddAttributes([new("deployment.environment.name", Builder.Environment.EnvironmentName)]))
            .WithTracing(Tracing =>
            {
                Tracing.AddSource(AiTelemetry.SourceName, AgentPlayground.Contracts.Messaging.CoachCallOutbox.ActivitySourceName,
                        DiagnosticHeaders.DefaultListenerName, "Npgsql")
                    .AddAspNetCoreInstrumentation(Options => Options.Filter = Context =>
                        !Context.Request.Path.StartsWithSegments("/health"))
                    .AddHttpClientInstrumentation()
                    .AddProcessor(new TelemetryPrivacyProcessor())
                    .AddProcessor<AiMetricsProcessor>();
                Tracing.SetSampler(Services => new ReloadingOtelSampler(Services.GetRequiredService<OtelRuntime>()))
                    .AddProcessor(Services => new BatchActivityExportProcessor(
                        new ReloadingOtelExporter<Activity>(Services.GetRequiredService<OtelRuntime>())));
            })
            .WithLogging(Options =>
            {
                Options.AddProcessor(new TelemetryLogPrivacyProcessor());
                Options.AddProcessor(Services => new BatchLogRecordExportProcessor(
                    new ReloadingOtelExporter<LogRecord>(Services.GetRequiredService<OtelRuntime>())));
            })
            .WithMetrics(Metrics =>
            {
                Metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation()
                    .AddMeter(InstrumentationOptions.MeterName, AiMetricsProcessor.MeterName);
                Metrics.AddReader(Services => new PeriodicExportingMetricReader(
                    new ReloadingOtelExporter<Metric>(Services.GetRequiredService<OtelRuntime>())));
            });
        return Builder;
    }
}

public sealed class TelemetryLogPrivacyProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord Record)
    {
        var exceptionType = Record.Exception?.GetType().FullName;
        Record.Body = "Application log (content redacted)";
        Record.FormattedMessage = null;
        Record.Exception = null;
        Record.Attributes = exceptionType is null ? [] : [new("error.type", exceptionType)];
    }
}

/// <summary>Only operational metadata may leave the process, including on failed SDK spans.</summary>
public sealed class TelemetryPrivacyProcessor : BaseProcessor<Activity>
{
    private static readonly HashSet<string> AllowedTags =
    [
        "http.request.method", "http.response.status_code", "http.route", "network.protocol.version",
        "server.address", "server.port", "db.system", "db.system.name", "db.operation.name",
        "messaging.system", "messaging.operation", "messaging.operation.name", "messaging.operation.type",
        "messaging.destination.name", "messaging.masstransit.message_type",
        "openinference.span.kind", "llm.model_name", "llm.system", "embedding.model_name", "tool.name",
        "llm.token_count.prompt", "llm.token_count.completion", "llm.token_count.total", "error.type"
    ];

    public override void OnEnd(Activity Activity)
    {
        foreach (var tag in Activity.TagObjects.ToArray())
            if (!AllowedTags.Contains(tag.Key)) Activity.SetTag(tag.Key, null);
        foreach (ref var item in Activity.EnumerateEvents())
            item = new ActivityEvent("event", item.Timestamp);
        foreach (ref var link in Activity.EnumerateLinks())
            link = new ActivityLink(link.Context);
        if (Activity.Status == ActivityStatusCode.Error) Activity.SetStatus(ActivityStatusCode.Error);
        // Npgsql names may contain SQL; HTTP names on unmatched routes may contain identifiers.
        if (Activity.Source.Name == "Npgsql") Activity.DisplayName = "postgresql";
        if (Activity.Source.Name.StartsWith("System.Net.Http", StringComparison.Ordinal)) Activity.DisplayName = "HTTP request";
        if (Activity.Source.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            Activity.DisplayName = Activity.GetTagItem("http.route") as string ?? "HTTP request";
    }
}
