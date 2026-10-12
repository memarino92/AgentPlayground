using System.Diagnostics;
using System.Globalization;
using PersonalAgent.Integrations;

namespace PersonalAgent.Backup;

public sealed class BackupDiagnostics
{
    private readonly Stopwatch Elapsed = Stopwatch.StartNew();
    private readonly Stopwatch StageElapsed = Stopwatch.StartNew();
    private readonly List<string> Secrets = [];
    private readonly Dictionary<string, string> Properties = new();
    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public string Stage { get; private set; } = "starting";
    public IIntegrationErrorClient? ErrorClient { get; set; }

    public void Protect(string? Value)
    {
        if (!string.IsNullOrEmpty(Value)) Secrets.Add(Value);
    }

    public string Clean(string Value)
    {
        foreach (var secret in Secrets.OrderByDescending(Secret => Secret.Length))
            Value = Value.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return SentryDetails.Sanitize(Value);
    }

    public void Context(string Key, object Value) => Properties[Key] = Clean(Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "");

    public void Begin(string Value)
    {
        Info($"Completed stage '{Stage}' duration_seconds={StageElapsed.Elapsed.TotalSeconds:F1}");
        Stage = Value;
        StageElapsed.Restart();
        Info("Starting stage");
    }

    public void Info(string Message) => Console.WriteLine(
        $"{DateTimeOffset.UtcNow:O} level=info run={RunId} stage='{Stage}' elapsed_seconds={Elapsed.Elapsed.TotalSeconds:F1} {Clean(Message)}");

    public void Failure(Exception Exception, string Message, int EventId = 1)
    {
        var details = Clean(Exception.ToString());
        Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} level=error run={RunId} stage='{Stage}' elapsed_seconds={Elapsed.Elapsed.TotalSeconds:F1} {Clean(Message)} {details}");
        var properties = new Dictionary<string, string>(Properties)
        {
            ["run_id"] = RunId, ["stage"] = Stage,
            ["elapsed_seconds"] = Elapsed.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)
        };
        try
        {
            var id = ErrorClient?.Capture(new("DatabaseBackup", EventId, Exception.GetType().FullName, null,
                Message: Clean(Message), MessageTemplate: EventId == 1 ? "Database backup failed" : "Database backup cleanup failed",
                ExceptionDetails: details, ExceptionMessage: Clean(Exception.Message),
                Frames: SentryDetails.ExceptionFrames(Exception), Properties: properties));
            Info(id is null ? "Sentry event unavailable (disabled, unconfigured, or capture failed)" : $"Sentry event queued: {id}");
        }
        catch { Info("Sentry event capture failed; preserving backup outcome"); }
    }
}
