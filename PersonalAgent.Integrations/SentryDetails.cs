using System.Collections;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PersonalAgent.Integrations;

internal static partial class SentryDetails
{
    private const string Hidden = "[redacted]";
    private const int MaxTextLength = 4096;

    public static string Sanitize(string? Value) => Clean(Value);
    public static bool IsCredentialField(string Name) => IsCredentialName(Name);

    public static (string Message, string? Template, IReadOnlyDictionary<string, string>? Properties) FromLog<TState>(
        TState State, Exception? Exception, Func<TState, Exception?, string> Formatter)
    {
        var fields = State as IEnumerable<KeyValuePair<string, object?>>;
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        string? template = null;
        if (fields is not null)
            foreach (var field in fields)
            {
                if (field.Key == "{OriginalFormat}") { template = Clean(field.Value?.ToString()); continue; }
                if (properties.Count >= 32) break;
                properties[field.Key] = IsCredentialName(field.Key) ? Hidden : Clean(SafeString(field.Value));
            }

        string? formatted;
        try { formatted = Formatter(State, Exception); }
        catch { formatted = template ?? State?.ToString(); }
        if (fields is not null)
            foreach (var field in fields)
            {
                if (!IsCredentialName(field.Key)) continue;
                var value = SafeString(field.Value);
                if (!string.IsNullOrEmpty(value)) formatted = formatted?.Replace(value, Hidden, StringComparison.Ordinal);
            }
        return (Clean(formatted), template, properties.Count == 0 ? null : properties);
    }

    public static string? FromException(Exception? Exception)
    {
        if (Exception is null) return null;
        var details = new List<string>();
        for (var current = Exception; current is not null && details.Count < 4; current = current.InnerException)
        {
            details.Add($"{current.GetType().FullName}: {Clean(current.Message)}");
            if (current.StackTrace is not null) details.Add(Clean(current.StackTrace));
        }
        return string.Join(Environment.NewLine, details);
    }

    public static string? ExceptionMessage(Exception? Exception) => Exception is null ? null : Clean(Exception.Message);

    public static IReadOnlyList<IntegrationFrame>? ExceptionFrames(Exception? Exception)
    {
        if (Exception is null) return null;
        var frames = new StackTrace(Exception, true).GetFrames();
        if (frames is null) return null;
        return frames.Take(64).Select(Frame => new IntegrationFrame(
            Frame.GetMethod()?.ToString() ?? "Unknown method",
            Frame.GetFileName() is { } path ? Clean(path) : null,
            Frame.GetFileLineNumber() is > 0 ? Frame.GetFileLineNumber() : null)).ToArray();
    }

    private static string? SafeString(object? Value)
    {
        if (Value is null) return null;
        if (Value is string text) return text;
        if (Value is IEnumerable and not string) return "[collection]";
        try { return Value.ToString(); }
        catch { return "[unprintable]"; }
    }

    private static bool IsCredentialName(string Name) => CredentialName().IsMatch(Name);

    private static string Clean(string? Value)
    {
        if (string.IsNullOrEmpty(Value)) return "Application error";
        var result = Value.Length > MaxTextLength ? Value[..MaxTextLength] : Value;
        result = Authorization().Replace(result, "$1" + Hidden);
        result = SecretAssignment().Replace(result, "$1" + Hidden);
        result = JsonSecret().Replace(result, "$1" + Hidden);
        result = UrlCredential().Replace(result, "$1" + Hidden + "@");
        result = Jwt().Replace(result, Hidden);
        result = PrivateKey().Replace(result, Hidden);
        return result;
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer|basic)\\s+)[^\\s,;]+", RegexOptions.NonBacktracking)]
    private static partial Regex Authorization();

    [GeneratedRegex("(?i)(\\b(?:password|passwd|pwd|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|secret|credential|dsn|token|key)\\s*(?:[:=]\\s*|\\s+)['\"]?)[^'\"\\s,;]+", RegexOptions.NonBacktracking)]
    private static partial Regex SecretAssignment();

    [GeneratedRegex("(https?://)[^/@\\s]+@", RegexOptions.NonBacktracking | RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredential();

    [GeneratedRegex("(?i)([\"'](?:password|passwd|pwd|api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|secret|credential|dsn|token|key)[\"']\\s*:\\s*[\"'])[^\"']+", RegexOptions.NonBacktracking)]
    private static partial Regex JsonSecret();

    [GeneratedRegex("\\beyJ[A-Za-z0-9_-]+\\.eyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\b", RegexOptions.NonBacktracking)]
    private static partial Regex Jwt();

    [GeneratedRegex("-----BEGIN [A-Z ]*PRIVATE KEY-----[\\s\\S]*", RegexOptions.NonBacktracking)]
    private static partial Regex PrivateKey();

    [GeneratedRegex("(?i)(^key$|password|passwd|pwd|api.?key|access.?token|refresh.?token|client.?secret|secret|credential|authorization|dsn|connection.?string|private.?key)", RegexOptions.NonBacktracking)]
    private static partial Regex CredentialName();
}
