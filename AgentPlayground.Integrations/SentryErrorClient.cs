using System.Globalization;
using Sentry;

namespace AgentPlayground.Integrations;

public sealed record IntegrationError(string Category, int EventId, string? ExceptionType, string? TraceId, bool Synthetic = false);

public interface IIntegrationErrorClient : IDisposable
{
    string? Capture(IntegrationError Error);
}

public interface IIntegrationErrorClientFactory
{
    IIntegrationErrorClient Create(IReadOnlyDictionary<string, string> Values, string Service);
}

public sealed class SentryErrorClientFactory(Func<SentryOptions, ISentryClient>? CreateClient = null) : IIntegrationErrorClientFactory
{
    public IIntegrationErrorClient Create(IReadOnlyDictionary<string, string> Values, string Service)
    {
        IntegrationRegistry.Validate(Values);
        var options = new SentryOptions
        {
            Dsn = Values["Dsn"], Environment = Values["Environment"],
            Release = System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown",
            ServerName = Service, SendDefaultPii = false, AttachStacktrace = false,
            AutoSessionTracking = false, SampleRate = float.Parse(Values["SampleRate"], CultureInfo.InvariantCulture),
            ShutdownTimeout = TimeSpan.FromSeconds(2), MaxQueueItems = 100
        };
        // A privately owned client avoids global SDK hooks and permits bounded replacement.
        // Only metadata is submitted; raw log values, exception messages, and request data never enter the SDK.
        return new SentryErrorClient(CreateClient?.Invoke(options) ?? new SentryClient(options), Service);
    }
}

public sealed class SentryErrorClient(ISentryClient Client, string Service) : IIntegrationErrorClient
{
    public string? Capture(IntegrationError Error)
    {
        var sentryEvent = CreateEvent(Error, Service);
        var id = Client.CaptureEvent(sentryEvent);
        return id == SentryId.Empty ? null : id.ToString();
    }

    public static SentryEvent CreateEvent(IntegrationError Error, string Service)
    {
        var sentryEvent = new SentryEvent
        {
            Message = Error.Synthetic ? "Synthetic integration test event" : "Application error (content redacted)",
            Level = Error.Synthetic ? SentryLevel.Info : SentryLevel.Error,
            Logger = Error.Category
        };
        sentryEvent.SetTag("service", Service);
        sentryEvent.SetTag("event_id", Error.EventId.ToString(CultureInfo.InvariantCulture));
        if (Error.ExceptionType is not null) sentryEvent.SetTag("exception_type", Error.ExceptionType);
        if (Error.TraceId is not null) sentryEvent.SetTag("trace_id", Error.TraceId);
        sentryEvent.SetFingerprint([Service, Error.Category, Error.EventId.ToString(CultureInfo.InvariantCulture), Error.ExceptionType ?? "log"]);
        return sentryEvent;
    }

    public void Dispose() => (Client as IDisposable)?.Dispose();
}
