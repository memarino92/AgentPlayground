using AgentPlayground.Contracts.Messaging.Responses;

namespace PersonalAgent.Services;

internal interface IWorkJournalParsingService
{
    Task<List<ParsedWorkJournalEntry>> ParseEntriesAsync(string FileName, string FileContent, CancellationToken CancellationToken = default);
}
