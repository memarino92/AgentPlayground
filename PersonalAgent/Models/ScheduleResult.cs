namespace PersonalAgent.Models;

public record ScheduleResult(Guid Id, DateTimeOffset ExecuteAtUtc, Guid CorrelationId, string Status);
