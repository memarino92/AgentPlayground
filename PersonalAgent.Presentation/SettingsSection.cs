namespace PersonalAgent.Presentation;

public sealed record SettingsSection(string Key, string Title)
{
    public static IReadOnlyList<SettingsSection> All { get; } =
    [
        new("sentry", "Sentry"),
        new("telemetry", "OpenTelemetry"),
        new("providers", "AI providers"),
        new("automation", "Railway sandboxes"),
        new("coding", "Platform coding"),
        new("database", "Database settings"),
        new("backup", "Database backups"),
        new("integrations", "Integrations and coach access")
    ];
}
