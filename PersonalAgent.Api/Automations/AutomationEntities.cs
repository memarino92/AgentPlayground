using MassTransit;

namespace PersonalAgent.Api.Automations;

internal sealed class AutomationDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string? ActorEmail { get; set; }
    public string SubjectProfileId { get; set; } = "";
    public string? SourceSessionId { get; set; }
    public string Status { get; set; } = "Active";
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public TimeSpan? Interval { get; set; }
}

internal sealed class AutomationVersion
{
    public Guid AutomationId { get; set; }
    public int Version { get; set; }
    public string Source { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class AutomationRun : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public Guid AutomationId { get; set; }
    public int Version { get; set; }
    public string CurrentState { get; set; } = "Initial";
    public int StepIndex { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public string? TraceId { get; set; }
}

internal sealed class AutomationStepExecution
{
    public Guid RunId { get; set; }
    public int Index { get; set; }
    public string StepId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public string? Output { get; set; }
    public string? Error { get; set; }
    public string? ProgramEvidence { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

// A real domain write: report creation commits with the saga transition and outgoing message intent.
internal sealed class AutomationReport
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int StepIndex { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
