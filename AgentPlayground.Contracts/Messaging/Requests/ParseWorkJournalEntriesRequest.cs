namespace AgentPlayground.Contracts.Messaging.Requests;

public record ParseWorkJournalEntriesRequest(Guid CorrelationId, string Source, string FileName, string MarkdownContent);
