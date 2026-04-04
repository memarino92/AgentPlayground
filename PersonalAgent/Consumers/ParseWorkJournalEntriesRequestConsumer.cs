using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using MassTransit;
using PersonalAgent.Services;

namespace PersonalAgent.Consumers;

internal class ParseWorkJournalEntriesRequestConsumer(
    WorkJournalParsingService parsingService,
    ILogger<ParseWorkJournalEntriesRequestConsumer> logger) : IConsumer<ParseWorkJournalEntriesRequest>
{
    public async Task Consume(ConsumeContext<ParseWorkJournalEntriesRequest> context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = context.Message.CorrelationId,
            ["Source"] = context.Message.Source,
            ["FileName"] = context.Message.FileName
        });

        logger.LogInformation("Received parse work journal entries request");
        var entries = await parsingService.ParseEntriesAsync(context.Message.FileName, context.Message.MarkdownContent, context.CancellationToken);
        logger.LogInformation("Parsed {EntryCount} journal entries for {FileName}", entries.Count, context.Message.FileName);

        await context.RespondAsync(new ParseWorkJournalEntriesResponse(entries));
    }
}
