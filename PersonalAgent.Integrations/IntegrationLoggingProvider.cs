using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Integrations;

[ProviderAlias("IntegrationErrors")]
public sealed class IntegrationLoggingProvider(IntegrationRuntime Runtime) : ILoggerProvider
{
    public ILogger CreateLogger(string CategoryName) => new ErrorLogger(Runtime, CategoryName);
    public void Dispose() { }

    private sealed class ErrorLogger(IntegrationRuntime Runtime, string Category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState State) where TState : notnull => null;
        public bool IsEnabled(LogLevel Level) => Level is LogLevel.Error or LogLevel.Critical
            && !Category.StartsWith("Sentry", StringComparison.Ordinal) && !Category.StartsWith("PersonalAgent.Integrations", StringComparison.Ordinal);
        public void Log<TState>(LogLevel LogLevel, EventId EventId, TState State, Exception? Exception, Func<TState, Exception?, string> Formatter)
        {
            if (!IsEnabled(LogLevel)) return;
            try
            {
                var (message, template, properties) = SentryDetails.FromLog(State, Exception, Formatter);
                Runtime.Capture(new(Category, EventId.Id, Exception?.GetType().FullName,
                    Activity.Current?.TraceId.ToString(), SpanId: Activity.Current?.SpanId.ToString(),
                    Message: message, MessageTemplate: template, ExceptionDetails: SentryDetails.FromException(Exception),
                    Properties: properties, Critical: LogLevel == LogLevel.Critical,
                    ExceptionMessage: SentryDetails.ExceptionMessage(Exception), Frames: SentryDetails.ExceptionFrames(Exception)));
            }
            catch { /* Error reporting must not interrupt application work. */ }
        }
    }
}

public static class IntegrationServiceExtensions
{
    public static IServiceCollection AddRuntimeIntegrations(this IServiceCollection Services, IConfiguration Configuration, string Service)
    {
        Services.AddSingleton(IntegrationDatabase.FromConfiguration(Configuration));
        Services.AddSingleton(new IntegrationHost(Service));
        Services.AddSingleton<IIntegrationSettingsStore, IntegrationSettingsStore>();
        Services.AddSingleton<IIntegrationErrorClientFactory, SentryErrorClientFactory>();
        Services.AddSingleton<IntegrationRuntime>();
        Services.AddSingleton<ILoggerProvider, IntegrationLoggingProvider>();
        Services.AddHostedService<IntegrationReloadWorker>();
        return Services;
    }
}
