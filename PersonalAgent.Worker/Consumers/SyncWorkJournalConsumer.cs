using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Events;
using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Worker.Configuration;
using Pgvector.Npgsql;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PersonalAgent.Worker.Consumers;

public class SyncWorkJournalConsumer(
    ILogger<SyncWorkJournalConsumer> logger,
    IOptions<GitHubOptions> githubOptions,
    IRequestClient<ParseWorkJournalEntriesRequest> parseRequestClient,
    IRequestClient<GenerateEmbeddingsRequest> embeddingRequestClient,
    IOptions<SqlTransportOptions> sqlOptions) : IConsumer<SyncWorkJournalCommand>
{
    private static readonly TimeSpan ParseRequestTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan EmbeddingRequestTimeout = TimeSpan.FromMinutes(2);
    private readonly GitHubOptions _options = githubOptions.Value;

    public async Task Consume(ConsumeContext<SyncWorkJournalCommand> context)
    {
        var correlationId = context.CorrelationId ?? context.MessageId ?? Guid.NewGuid();
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["MessageId"] = context.MessageId ?? Guid.Empty,
            ["Source"] = "PersonalAgent.Worker"
        });

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogInformation("Starting work journal sync from GitHub");

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _options.PersonalAccessToken);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PersonalAgent", "1.0"));

        var url = $"https://api.github.com/repos/{_options.RepoOwner}/{_options.RepoName}/contents/{_options.JournalPath}?ref={_options.Branch}";
        
        logger.LogInformation("Fetching journal contents from {Url}", url);
        var response = await client.GetAsync(url, context.CancellationToken);
        
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Failed to fetch journal contents. Status: {Status}", response.StatusCode);
            return;
        }

        var contentsJson = await response.Content.ReadAsStringAsync(context.CancellationToken);
        using var document = JsonDocument.Parse(contentsJson);

        var markdownFiles = document.RootElement.EnumerateArray()
            .Where(element =>
                string.Equals(element.GetProperty("type").GetString(), "file", StringComparison.OrdinalIgnoreCase)
                && element.GetProperty("name").GetString()?.EndsWith(".md", StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        logger.LogInformation("Found {MarkdownFileCount} markdown files in journal path {JournalPath}", markdownFiles.Count, _options.JournalPath);
        
        int entriesSynced = 0;
        int filesFailed = 0;
        int entriesSkippedUnchanged = 0;
        int entriesParsed = 0;
        var connectionString = sqlOptions.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("SqlTransportOptions:ConnectionString is required");

        await using var dataSource = CreateVectorDataSource(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(context.CancellationToken);
        
        // Ensure pgvector is loaded
        await using (var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", connection))
        {
            await cmd.ExecuteNonQueryAsync(context.CancellationToken);
        }
        await using (var cmd = new NpgsqlCommand(@"
            CREATE TABLE IF NOT EXISTS work_journal_entries (
                id uuid PRIMARY KEY,
                entry_date date NOT NULL,
                file_name text NOT NULL,
                content text NOT NULL,
                embedding vector(1536)
            );
            CREATE INDEX IF NOT EXISTS ix_work_journal_entries_embedding ON work_journal_entries USING hnsw (embedding vector_cosine_ops);
        ", connection))
        {
            await cmd.ExecuteNonQueryAsync(context.CancellationToken);
        }
        
        connection.ReloadTypes(); 
        logger.LogInformation("Initialized pgvector infrastructure for work journal sync");

        foreach (var element in markdownFiles)
        {
            var name = element.GetProperty("name").GetString();
            var downloadUrl = element.GetProperty("download_url").GetString();

            if (name is null || downloadUrl is null)
            {
                logger.LogWarning("Skipping journal file because name or download URL is missing");
                continue;
            }

            try
            {
                logger.LogInformation("Downloading journal file {FileName}", name);
                var fileContent = await client.GetStringAsync(downloadUrl, context.CancellationToken);

                logger.LogInformation("Requesting journal parsing for {FileName}", name);
                var parseResponse = await RequestWithTimeoutAsync(
                    requestCancellationToken => parseRequestClient.GetResponse<ParseWorkJournalEntriesResponse>(
                        new ParseWorkJournalEntriesRequest(correlationId, "PersonalAgent.Worker", name, fileContent),
                        requestCancellationToken),
                    context.CancellationToken,
                    ParseRequestTimeout);

                var entries = parseResponse.Message.Entries;
                entriesParsed += entries.Count;
                logger.LogInformation("Parsed {EntryCount} entries from {FileName}", entries.Count, name);

                var changedEntries = new List<(Guid Id, DateTime Date, string Content)>();
                foreach (var entry in entries)
                {
                    var idString = $"{name}-{entry.Date:yyyy-MM-dd}";
                    using var md5 = System.Security.Cryptography.MD5.Create();
                    var idBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(idString));
                    var id = new Guid(idBytes);

                    var existingContent = await GetExistingContentAsync(connection, id, context.CancellationToken);
                    if (existingContent == entry.Content)
                    {
                        entriesSkippedUnchanged++;
                        continue;
                    }

                    changedEntries.Add((id, entry.Date, entry.Content));
                }

                if (changedEntries.Count == 0)
                {
                    logger.LogInformation("No changed entries found in {FileName}; skipping embedding generation", name);
                    continue;
                }

                logger.LogInformation("Requesting {ChangedEntryCount} embeddings for {FileName}", changedEntries.Count, name);
                var embeddingResponse = await RequestWithTimeoutAsync(
                    requestCancellationToken => embeddingRequestClient.GetResponse<GenerateEmbeddingsResponse>(
                        new GenerateEmbeddingsRequest(correlationId, "PersonalAgent.Worker", changedEntries.Select(entry => entry.Content).ToList()),
                        requestCancellationToken),
                    context.CancellationToken,
                    EmbeddingRequestTimeout);

                var embeddings = embeddingResponse.Message.Embeddings;
                if (embeddings.Count != changedEntries.Count)
                    throw new InvalidOperationException($"Embedding count mismatch for {name}. Expected {changedEntries.Count}, received {embeddings.Count}.");

                for (var index = 0; index < changedEntries.Count; index++)
                {
                    var changedEntry = changedEntries[index];
                    await UpsertEntryAsync(connection, changedEntry.Id, name, changedEntry.Date, changedEntry.Content, embeddings[index], context.CancellationToken);
                    entriesSynced++;
                }

                logger.LogInformation(
                    "Finished processing {FileName}. Parsed: {ParsedCount}, Changed: {ChangedCount}, Upserted: {UpsertedCount}",
                    name,
                    entries.Count,
                    changedEntries.Count,
                    changedEntries.Count);
            }
            catch (RequestFaultException ex)
            {
                filesFailed++;
                logger.LogWarning(ex, "Model request failed for file {FileName}; continuing with remaining files", name);
            }
            catch (RequestTimeoutException ex)
            {
                filesFailed++;
                logger.LogWarning(ex, "Model request timed out for file {FileName}; continuing with remaining files", name);
            }
            catch (OperationCanceledException ex) when (!context.CancellationToken.IsCancellationRequested)
            {
                filesFailed++;
                logger.LogWarning(ex, "Model request hit local timeout for file {FileName}; continuing with remaining files", name);
            }
            catch (Exception ex)
            {
                filesFailed++;
                logger.LogError(ex, "Unexpected error while processing file {FileName}; continuing with remaining files", name);
            }
        }

        stopwatch.Stop();
        logger.LogInformation(
            "Finished work journal sync. Files: {FileCount}, FailedFiles: {FailedFileCount}, ParsedEntries: {ParsedEntries}, SkippedUnchanged: {SkippedUnchanged}, SyncedEntries: {SyncedEntries}, StartedAtUtc: {StartedAtUtc}, DurationMs: {DurationMs}",
            markdownFiles.Count,
            filesFailed,
            entriesParsed,
            entriesSkippedUnchanged,
            entriesSynced,
            startedAt,
            stopwatch.ElapsedMilliseconds);
        await context.Publish(new WorkJournalSyncedEvent(entriesSynced, DateTime.UtcNow), context.CancellationToken);
        logger.LogInformation("Published WorkJournalSyncedEvent with {EntriesSynced} synced entries", entriesSynced);
    }

    private async Task<string?> GetExistingContentAsync(NpgsqlConnection connection, Guid id, CancellationToken cancellationToken)
    {
        var sql = "SELECT content FROM work_journal_entries WHERE id = @id";
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", id);
        return (string?)await cmd.ExecuteScalarAsync(cancellationToken);
    }

    private async Task UpsertEntryAsync(NpgsqlConnection connection, Guid id, string fileName, DateTime date, string content, float[] embedding, CancellationToken cancellationToken)
    {
        var sql = @"
            INSERT INTO work_journal_entries (id, entry_date, file_name, content, embedding)
            VALUES (@id, @date, @fileName, @content, @embedding)
            ON CONFLICT (id) DO UPDATE SET 
                entry_date = EXCLUDED.entry_date,
                file_name = EXCLUDED.file_name,
                content = EXCLUDED.content,
                embedding = EXCLUDED.embedding;";

        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("date", date);
        cmd.Parameters.AddWithValue("fileName", fileName);
        cmd.Parameters.AddWithValue("content", content);
        cmd.Parameters.Add(new NpgsqlParameter("embedding", new Pgvector.Vector(embedding)) { DataTypeName = "vector" });

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static NpgsqlDataSource CreateVectorDataSource(string connectionString)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        return dataSourceBuilder.Build();
    }

    internal static async Task<TResponse> RequestWithTimeoutAsync<TResponse>(
        Func<CancellationToken, Task<TResponse>> request,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        using var timeoutCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellationTokenSource.CancelAfter(timeout);
        return await request(timeoutCancellationTokenSource.Token);
    }
}
