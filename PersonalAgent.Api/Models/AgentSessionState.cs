namespace PersonalAgent.Api.Models;

internal record AgentSessionState(string ModelId, Guid? ScheduledTaskId = null)
{
    public bool IsContinuous { get; init; }
    public long ContextStartSequence { get; init; }
    public DateTimeOffset? RecallAfter { get; init; }
}
