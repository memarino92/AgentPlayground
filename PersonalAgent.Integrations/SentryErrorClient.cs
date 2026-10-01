using System.Globalization;
using Sentry;
using Sentry.Protocol;

namespace PersonalAgent.Integrations;

public sealed record IntegrationError(string Category, int EventId, string? ExceptionType, string? TraceId,
    bool Synthetic = false, string? SpanId = null, string? Message = null, string? MessageTemplate = null,
    string? ExceptionDetails = null, IReadOnlyDictionary<string, string>? Properties = null, bool Critical = false,
    string? ExceptionMessage = null, IReadOnlyList<IntegrationFrame>? Frames = null);

public sealed record IntegrationFrame(string Function, string? FileName, int? LineNumber);

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
        // The logger sanitizes application details before they enter the privately owned SDK client.
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
            Message = Error.Synthetic ? "Synthetic integration test event" : SentryDetails.Sanitize(Error.Message ?? Error.MessageTemplate),
            Level = Error.Synthetic ? SentryLevel.Info : Error.Critical ? SentryLevel.Fatal : SentryLevel.Error,
            Logger = Error.Category
        };
        sentryEvent.SetTag("service", Service);
        sentryEvent.SetTag("event_id", Error.EventId.ToString(CultureInfo.InvariantCulture));
        if (Error.ExceptionType is not null) sentryEvent.SetTag("exception_type", Error.ExceptionType);
        if (Error.ExceptionType is not null)
            sentryEvent.SentryExceptions =
            [
                new SentryException
                {
                    Type = Error.ExceptionType,
                    Value = Error.ExceptionMessage is null ? null : SentryDetails.Sanitize(Error.ExceptionMessage),
                    Stacktrace = Error.Frames is { Count: > 0 }
                        ? new SentryStackTrace
                        {
                            Frames = Error.Frames.Reverse().Select(Frame => new SentryStackFrame
                            {
                                Function = SentryDetails.Sanitize(Frame.Function),
                                FileName = Frame.FileName is null ? null : SentryDetails.Sanitize(Frame.FileName),
                                LineNumber = Frame.LineNumber
                            }).ToList()
                        } : null
                }
            ];
        if (Error.MessageTemplate is not null) sentryEvent.SetExtra("message_template", SentryDetails.Sanitize(Error.MessageTemplate));
        if (Error.ExceptionDetails is not null) sentryEvent.SetExtra("exception_details", SentryDetails.Sanitize(Error.ExceptionDetails));
        if (Error.Properties is not null)
            foreach (var property in Error.Properties)
                sentryEvent.SetExtra($"log.{property.Key}", SentryDetails.IsCredentialField(property.Key)
                    ? "[redacted]" : SentryDetails.Sanitize(property.Value));
        if (Error.TraceId is not null) sentryEvent.SetTag("trace_id", Error.TraceId);
        if (Error.SpanId is not null) sentryEvent.SetTag("span_id", Error.SpanId);
        if (Guid.TryParseExact(Error.TraceId, "N", out var traceId) && Error.SpanId is { Length: 16 })
        {
            sentryEvent.Contexts.Trace.TraceId = new SentryId(traceId);
            sentryEvent.Contexts.Trace.SpanId = new SpanId(Error.SpanId);
        }
        sentryEvent.SetFingerprint([Service, Error.Category, Error.EventId.ToString(CultureInfo.InvariantCulture),
            Error.ExceptionType ?? "log", SentryDetails.Sanitize(Error.MessageTemplate ?? Error.Message)]);
        return sentryEvent;
    }

    public void Dispose() => (Client as IDisposable)?.Dispose();
}
