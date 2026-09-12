using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using Pgvector.Npgsql;

namespace PersonalAgent.Services;

internal class WorkJournalService(
    IBus bus,
    IOptions<SqlTransportOptions> sqlOptions,
    IAgentEmbeddingService embeddingService,
    ILogger<WorkJournalService> logger)
{
    private readonly string _connectionString = sqlOptions.Value.ConnectionString ?? string.Empty;

    public async Task<string> SyncWorkJournalAsync(string reason = "User requested", CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Publishing SyncWorkJournalCommand. Reason: {Reason}", reason);
        await bus.Publish(new SyncWorkJournalCommand(), cancellationToken);
        return "Work journal sync command has been published and is running in the background.";
    }

    public Task<string> SearchWorkJournalAsync(string query, CancellationToken cancellationToken = default)
        => AgentPlayground.Integrations.AiTelemetry.RunAsync("journal.retrieve", "RETRIEVER",
            () => SearchWorkJournalCoreAsync(query, cancellationToken));

    private async Task<string> SearchWorkJournalCoreAsync(string query, CancellationToken cancellationToken)
    {
        logger.LogInformation("Searching work journal for: {Query}", query);
        
        try
        {
            var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
            var queryVector = new Pgvector.Vector(queryEmbedding.ToArray());

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(_connectionString);
            dataSourceBuilder.UseVector();
            await using var dataSource = dataSourceBuilder.Build();
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

            var sql = @"
                SELECT entry_date, content 
                FROM work_journal_entries 
                ORDER BY embedding <=> @query_embedding 
                LIMIT 5;";

            await using var cmd = new NpgsqlCommand(sql, connection);
            cmd.Parameters.Add(new NpgsqlParameter("query_embedding", queryVector) { DataTypeName = "vector" });

            var results = new List<string>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var date = reader.GetDateTime(0);
                var content = reader.GetString(1);
                results.Add($"Date: {date:yyyy-MM-dd}\nContent:\n{content}");
            }

            if (results.Count == 0)
            {
                return "No relevant work journal entries found.";
            }

            return "Found the following journal entries:\n\n" + string.Join("\n\n---\n\n", results);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01") // undefined_table
        {
            logger.LogWarning(ex, "work_journal_entries table does not exist yet.");
            return "The work journal database hasn't been initialized or synced yet. Please sync the work journal first.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error searching work journal.");
            return $"An error occurred while searching the work journal: {ex.Message}";
        }
    }
}
