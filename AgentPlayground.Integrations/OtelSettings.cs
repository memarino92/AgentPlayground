using System.Globalization;

namespace AgentPlayground.Integrations;

public static class OtelSettings
{
    public static IReadOnlyList<IntegrationField> Fields { get; } =
    [
        new("Enabled", "Enable telemetry export", "boolean", false, "false"),
        new("TracesEnabled", "Export traces", "boolean", false, "true"),
        new("MetricsEnabled", "Export metrics", "boolean", false, "false"),
        new("LogsEnabled", "Export logs", "boolean", false, "false"),
        new("Endpoint", "Collector endpoint", "text", false, ""),
        new("Protocol", "Protocol", "text", false, "grpc"),
        new("Headers", "Collector authentication headers", "password", true, ""),
        new("SampleRate", "Trace sample rate (0–1)", "number", false, "1"),
        new("TimeoutMilliseconds", "Export timeout (milliseconds)", "number", false, "3000")
    ];

    public static Dictionary<string, string> Defaults() => Fields.ToDictionary(Field => Field.Key, Field => Field.DefaultValue);
    public static Dictionary<string, string> Merge(Dictionary<string, string> Saved, Dictionary<string, string> Changes)
    {
        var values = new Dictionary<string, string>(Saved);
        foreach (var (key, value) in Changes)
        {
            if (!Fields.Any(Field => Field.Key == key)) throw new IntegrationValidationException(new() { [key] = ["Unsupported setting."] });
            values[key] = value?.Trim() ?? "";
        }
        Validate(values);
        return values;
    }

    public static void Validate(IReadOnlyDictionary<string, string> Values)
    {
        var errors = new Dictionary<string, string[]>();
        if (!bool.TryParse(Values.GetValueOrDefault("Enabled"), out var enabled)) errors["Enabled"] = ["Choose true or false."];
        foreach (var key in new[] { "TracesEnabled", "MetricsEnabled", "LogsEnabled" })
            if (!bool.TryParse(Values.GetValueOrDefault(key), out _)) errors[key] = ["Choose true or false."];
        var endpoint = Values.GetValueOrDefault("Endpoint") ?? "";
        // Private collector hosts are intentional; only deployment administrators can set this destination.
        if ((enabled || endpoint.Length > 0) && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0))
            errors["Endpoint"] = ["Enter an HTTP or HTTPS collector base URL without user information, query or fragment."];
        if (Values.GetValueOrDefault("Protocol") is not ("grpc" or "http/protobuf")) errors["Protocol"] = ["Choose grpc or http/protobuf."];
        if (!double.TryParse(Values.GetValueOrDefault("SampleRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
            || !double.IsFinite(rate) || rate is < 0 or > 1) errors["SampleRate"] = ["Enter a number from 0 to 1."];
        if (!int.TryParse(Values.GetValueOrDefault("TimeoutMilliseconds"), out var timeout) || timeout is < 100 or > 10000)
            errors["TimeoutMilliseconds"] = ["Enter a timeout from 100 to 10000 milliseconds."];
        var headers = Values.GetValueOrDefault("Headers") ?? "";
        if (headers.Length > 2048 || headers.Any(Char.IsControl) || headers.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(Header =>
            Header.IndexOf('=') < 1 || !Header[..Header.IndexOf('=')].All(Character => Char.IsAsciiLetterOrDigit(Character) || Character == '-')))
            errors["Headers"] = ["Use comma-separated header-name=value pairs without control characters (maximum 2048 characters)."];
        if (headers.Length > 0 && !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            errors["Headers"] = ["Use HTTPS when sending collector authentication headers."];
        if (errors.Count > 0) throw new IntegrationValidationException(errors);
    }
}
