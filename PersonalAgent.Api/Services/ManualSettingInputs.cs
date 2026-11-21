namespace PersonalAgent.Api.Services;

// Stable UI inputs, including revision-backed settings that are not database-editor rows.
// No values, credentials, or permissions are carried in these links.
internal static class ManualSettingInputs
{
    private static readonly IReadOnlyDictionary<string, string[]> Sections = new Dictionary<string, string[]>
    {
        ["providers"] = ["openrouter-api-key"],
        ["sentry"] = ["sentry-enabled", "sentry-environment", "sentry-sample", "sentry-secret-action"],
        ["telemetry"] = ["otel-enabled", "otel-traces", "otel-metrics", "otel-logs", "otel-endpoint", "otel-protocol", "otel-secret-action", "otel-rate", "otel-timeout"],
        ["backup"] = ["backup-enabled", "backup-host", "backup-port", "backup-username", "backup-password", "backup-fingerprint", "backup-directory", "backup-interval"],
        ["automation"] = ["runner-enabled", "runner-token-action", "runner-environment", "runner-gateway", "runner-checkpoint", "runner-image", "runner-review", "runner-packages"],
        ["coding"] = ["coding-enabled", "coding-model", "coding-repo", "coding-checkpoint", "coding-image", "coding-app", "coding-installation", "coding-ruleset", "coding-ruleset-updated", "coding-router-action", "coding-github-action", "coding-minutes", "coding-requests", "coding-tests", "coding-filter"]
    };

    public static IEnumerable<SettingInputLink> Find(string Key, string? Scope) => Scope is null or "Manual"
        ? Sections.SelectMany(Section => Section.Value.Select(Id => new SettingInputLink("Manual", "input:" + Id, false,
            $"/admin/settings?section={Section.Key}#{Id}"))).Where(Input => Key.Length == 0 || Input.Key == Key)
        : [];
}

internal sealed record SettingInputLink(string Scope, string Key, bool AgentEditable, string Link);
