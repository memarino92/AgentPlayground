namespace PersonalAgent.Models;

internal record AgentSessionState(string ModelId, Guid? ScheduledTaskId = null);
