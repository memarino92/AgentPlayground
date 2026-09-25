using System.Text.Json;

namespace PersonalAgent.Contracts.Automations;

/// <summary>A versioned, bounded recipe. Execution never calls a chat model.</summary>
public sealed record AutomationRecipe(IReadOnlyList<AutomationStep> Steps);
public sealed record AutomationStep(string Id, string Action, JsonElement Arguments, AutomationCondition? When = null);
public sealed record AutomationCondition(string Step, [property: System.Text.Json.Serialization.JsonPropertyName("equals")] string Expected);
public sealed record SaveAutomationRequest(string Name, string Source, DateTimeOffset? ExecuteAt = null, string? RepeatEvery = null, int? ExpectedVersion = null);
public sealed record AutomationSummary(Guid Id, string Name, string Status, int Version, DateTimeOffset CreatedAt,
    DateTimeOffset? NextRunAt, TimeSpan? Interval, string SubjectProfileId, string ActorId);
public sealed record AutomationVersionResponse(int Version, string Source, string Hash, DateTimeOffset CreatedAt);
public sealed record AutomationRunResponse(Guid Id, int Version, string Status, DateTimeOffset ScheduledAt,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error, string? TraceId);
public sealed record AutomationStepResponse(int Index, string Id, string Action, string Status, string? Output, string? Error, DateTimeOffset? CompletedAt, string? ProgramEvidence = null);
public sealed record AutomationReportResponse(Guid Id, string Title, string Content, DateTimeOffset CreatedAt);
public sealed record AutomationDetail(AutomationSummary Automation, IReadOnlyList<AutomationVersionResponse> Versions,
    IReadOnlyList<AutomationRunResponse> Runs, IReadOnlyList<DateTimeOffset> UpcomingRuns);
public sealed record AutomationRunDetail(AutomationRunResponse Run, IReadOnlyList<AutomationStepResponse> Steps, IReadOnlyList<AutomationReportResponse> Reports);
public sealed record AutomationActionDescriptor(string Action, string Description, object Arguments);

// Commands contain identities only. Executable source and authority are loaded from persisted records.
public sealed record StartAutomation(Guid RunId);
public sealed record ExecuteAutomationStep(Guid RunId, int StepIndex);
public sealed record AutomationStepCompleted(Guid RunId, int StepIndex, string Output, bool Skipped = false, AutomationProgramEvidence? Program = null);
public sealed record AutomationStepFailed(Guid RunId, int StepIndex, string Error, bool Blocked = false, AutomationProgramEvidence? Program = null);
