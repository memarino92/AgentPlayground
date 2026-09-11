using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentPlayground.Integrations;

public static partial class IntegrationRegistry
{
    public const string SentryId = "sentry";
    public static IReadOnlyList<IntegrationField> Fields { get; } =
    [
        new("Enabled", "Enable error reporting", "boolean", false, "false"),
        new("Dsn", "Project DSN", "password", true, ""),
        new("Environment", "Environment", "text", false, "production"),
        new("SampleRate", "Error sample rate (0–1)", "number", false, "1")
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
        var dsn = Values.GetValueOrDefault("Dsn") ?? "";
        if ((enabled || dsn.Length > 0) && !IsAllowedDsn(dsn)) errors["Dsn"] = ["Enter a Sentry hosted HTTPS DSN with a public key and numeric project ID."];
        var environment = Values.GetValueOrDefault("Environment") ?? "";
        if (!EnvironmentPattern().IsMatch(environment)) errors["Environment"] = ["Use 1–64 letters, numbers, dots, underscores, or hyphens."];
        if (!double.TryParse(Values.GetValueOrDefault("SampleRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
            || !double.IsFinite(rate) || rate < 0 || rate > 1) errors["SampleRate"] = ["Enter a number from 0 to 1."];
        if (errors.Count > 0) throw new IntegrationValidationException(errors);
    }

    public static bool IsAllowedDsn(string Value) => Uri.TryCreate(Value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Port == 443 && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && HostPattern().IsMatch(uri.Host) && PublicKeyPattern().IsMatch(uri.UserInfo)
        && ProjectPattern().IsMatch(uri.AbsolutePath);

    public static (Dictionary<string, string> Values, string[] Overrides) ResolveOverrides(Dictionary<string, string> Values, Func<string, string?> ReadEnvironment)
    {
        var effective = new Dictionary<string, string>(Values);
        var overrides = new List<string>();
        foreach (var (key, variable) in new[] { ("Enabled", "SENTRY_ENABLED"), ("Dsn", "SENTRY_DSN"), ("Environment", "SENTRY_ENVIRONMENT"), ("SampleRate", "SENTRY_SAMPLE_RATE") })
        {
            var value = ReadEnvironment(variable);
            if (string.IsNullOrWhiteSpace(value)) continue;
            effective[key] = value.Trim();
            overrides.Add(variable);
        }
        return (effective, [.. overrides]);
    }

    [GeneratedRegex(@"^o[0-9]+\.ingest(?:\.(?:us|de))?\.sentry\.io$", RegexOptions.CultureInvariant)]
    private static partial Regex HostPattern();
    [GeneratedRegex("^[a-fA-F0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicKeyPattern();
    [GeneratedRegex(@"^/[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectPattern();
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentPattern();
}
