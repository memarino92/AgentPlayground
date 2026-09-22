using PersonalAgent.Contracts.Messaging.Responses;

namespace PersonalAgent.Api.Services;

internal interface IWorkJournalParsingService
{
    Task<List<ParsedWorkJournalEntry>> ParseEntriesAsync(string FileName, string FileContent, CancellationToken CancellationToken = default);
}
