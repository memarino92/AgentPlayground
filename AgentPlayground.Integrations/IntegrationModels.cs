namespace AgentPlayground.Integrations;

/// <summary>A supported integration field; secret values are never returned.</summary>
public sealed record IntegrationField(string Key, string Label, string Kind, bool Secret, string DefaultValue);
/// <summary>A revision-safe edit. Missing secret fields retain their saved value; empty values clear them.</summary>
public sealed record SaveIntegrationRequest(long ExpectedRevision, Dictionary<string, string> Values);
/// <summary>Apply the exact saved revision after review.</summary>
public sealed record ApplyIntegrationRequest(long Revision);
/// <summary>Public settings, with secrets replaced by configured-state indicators.</summary>
public sealed record IntegrationSettingsResponse(string Id, long SavedRevision, long ActiveRevision,
    IReadOnlyList<IntegrationField> Fields, Dictionary<string, string> Values, string[] ConfiguredSecrets,
    IReadOnlyList<IntegrationInstanceResponse> Instances);
/// <summary>Last acknowledged runtime revision and health for a service instance.</summary>
public sealed record IntegrationInstanceResponse(string Service, string Instance, long Revision, string Status,
    string[] Overrides, DateTimeOffset LastSeen);
/// <summary>A synthetic test event was queued locally; ingestion must be checked in Sentry.</summary>
public sealed record IntegrationTestResponse(string EventId, string Status);

public sealed record IntegrationRevision(long SavedRevision, long ActiveRevision, Dictionary<string, string> Values);
public sealed class IntegrationValidationException(Dictionary<string, string[]> Errors) : Exception("Integration settings are invalid.")
{
    public Dictionary<string, string[]> Errors { get; } = Errors;
}
public sealed class IntegrationConflictException() : Exception("Settings changed. Refresh before saving or applying.");
