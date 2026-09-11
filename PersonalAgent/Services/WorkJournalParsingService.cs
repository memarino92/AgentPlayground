using AgentPlayground.Contracts.Messaging.Responses;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using PersonalAgent.Configuration;
using System.Text.Json;

namespace PersonalAgent.Services;

internal class WorkJournalParsingService : IWorkJournalParsingService
{
    private readonly OpenAiClientProvider _clients;
    private readonly IChatModelCatalog _chatModelCatalog;
    private readonly ILogger<WorkJournalParsingService> _logger;

    public WorkJournalParsingService(
        IOptions<ApiKeyOptions> apiKeyOptions,
        IChatModelCatalog chatModelCatalog,
        ILogger<WorkJournalParsingService> logger)
    {
        _clients = new(apiKeyOptions);
        _chatModelCatalog = chatModelCatalog;
        _logger = logger;
    }

    public async Task<List<ParsedWorkJournalEntry>> ParseEntriesAsync(string fileName, string fileContent, CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
        You are a helpful data extraction assistant.
        Parse the following work journal markdown file into distinct entries.
        The filename is '{{fileName}}', which indicates the year and month (e.g. 2026_01.md implies Jan 2026).
        Entries start with '## ' headings that represent dates or date ranges (e.g. '## 01/2' or '## 01/4-5').
        Each '## ' heading must produce exactly one entry.
        The "date" field must contain a single date in YYYY-MM-DD format derived from the heading using the year and month from the filename.
        If a heading is a date range, use the first date in the range for the "date" field. For example, '## 01/4-5' maps to 'YYYY-01-04'.
        If a heading uses any shorthand or otherwise ambiguous form that could imply multiple dates, always use the earliest implied date as the single "date" value.
        Do not expand date ranges into multiple entries.
        Preserve the full original markdown for that heading in "content", including the heading and bullet points.
        
        Return a JSON object with the following structure exactly:
        {
            "entries": [
                {
                    "date": "YYYY-MM-DD",
                    "content": "The full markdown content of the entry, including the heading and bullet points."
                }
            ]
        }

        Markdown content:
        {{fileContent}}
        """;

        var model = await _chatModelCatalog.GetDefaultModelAsync(cancellationToken);
        _logger.LogInformation("Parsing work journal with model {ModelId}", model.Id);
        var response = await _clients.Current.GetChatClient(model.Id).CompleteChatAsync(
            [new UserChatMessage(prompt)],
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            cancellationToken);

        var json = response.Value.Content[0].Text;
        using var document = JsonDocument.Parse(json);
        var entries = new List<ParsedWorkJournalEntry>();

        if (!document.RootElement.TryGetProperty("entries", out var entriesElement)) return entries;

        foreach (var element in entriesElement.EnumerateArray())
        {
            if (!element.TryGetProperty("date", out var dateElement)) continue;
            if (!element.TryGetProperty("content", out var contentElement)) continue;
            if (!DateTime.TryParse(dateElement.GetString(), out var date)) continue;
            entries.Add(new ParsedWorkJournalEntry(date, contentElement.GetString() ?? string.Empty));
        }

        return entries;
    }
}
