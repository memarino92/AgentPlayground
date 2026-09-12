using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentPlayground.Integrations;

[ProviderAlias("IntegrationErrors")]
public sealed class IntegrationLoggingProvider(IntegrationRuntime Runtime) : ILoggerProvider
{
    public ILogger CreateLogger(string CategoryName) => new ErrorLogger(Runtime, CategoryName);
    public void Dispose() { }

    private sealed class ErrorLogger(IntegrationRuntime Runtime, string Category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState State) where TState : notnull => null;
        public bool IsEnabled(LogLevel Level) => Level is LogLevel.Error or LogLevel.Critical
            && !Category.StartsWith("Sentry", StringComparison.Ordinal) && !Category.StartsWith("AgentPlayground.Integrations", StringComparison.Ordinal);
        public void Log<TState>(LogLevel LogLevel, EventId EventId, TState State, Exception? Exception, Func<TState, Exception?, string> Formatter)
        {
            if (IsEnabled(LogLevel)) Runtime.Capture(new(Category, EventId.Id, Exception?.GetType().FullName,
                Activity.Current?.TraceId.ToString(), SpanId: Activity.Current?.SpanId.ToString()));
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
