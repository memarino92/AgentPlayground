using PersonalAgent.Contracts.Messaging.Responses;

using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Development;

internal sealed class SyntheticJournalParsingService : IWorkJournalParsingService
{
    public Task<List<ParsedWorkJournalEntry>> ParseEntriesAsync(string FileName, string FileContent, CancellationToken CancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        // A documented fixed fixture, never a claim that arbitrary journal text was parsed.
        return Task.FromResult<List<ParsedWorkJournalEntry>>([new(new DateTime(2026, 9, 10), "## 09/10\nSynthetic journal: rehearsed local startup and recovery.")]);
    }
}
