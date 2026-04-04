namespace AgentPlayground.Contracts.Messaging.Responses;

public record ParseWorkJournalEntriesResponse(List<ParsedWorkJournalEntry> Entries);

public record ParsedWorkJournalEntry(DateTime Date, string Content);
