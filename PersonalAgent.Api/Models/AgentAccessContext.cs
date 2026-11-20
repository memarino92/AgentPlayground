namespace PersonalAgent.Api.Models;

internal record AgentAccessContext(string ActorId, string Role, string SubjectProfileId)
{
    public string? Email { get; init; }
    public string? SessionId { get; init; }
    public Guid? ScheduledTaskId { get; init; }
    public ScheduledRunContext? ScheduledRun { get; init; }
    public Func<PersonalAgent.Contracts.ChatToolActivity, CancellationToken, ValueTask>? ToolActivity { get; init; }
    public Func<string, CancellationToken, ValueTask>? ScratchOutput { get; init; }
    public string MemoryProfileId => Role == AgentRoles.Owner ? SubjectProfileId : $"actor:{ActorId}";
}

internal sealed class ScheduledRunContext
{
    public bool AuthorizationDenied { get; set; }
}
