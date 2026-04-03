using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using PersonalAgent.Worker.Configuration;
using Pgvector.Npgsql;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalAgent.Worker.Consumers;

public class SyncWorkJournalConsumer(
    ILogger<SyncWorkJournalConsumer> logger,
    IOptions<GitHubOptions> githubOptions,
    IOptions<ApiKeyOptions> apiKeyOptions,
    IOptions<SqlTransportOptions> sqlOptions) : IConsumer<SyncWorkJournalCommand>
{
    private readonly GitHubOptions _options = githubOptions.Value;
    private readonly EmbeddingClient _embeddingClient = new OpenAIClient(apiKeyOptions.Value.OpenAiKey).GetEmbeddingClient("text-embedding-3-small");
    private readonly ChatClient _chatClient = new OpenAIClient(apiKeyOptions.Value.OpenAiKey).GetChatClient("gpt-4o-mini");

    public async Task Consume(ConsumeContext<SyncWorkJournalCommand> context)
    {
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
        
        int entriesSynced = 0;
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(sqlOptions.Value.ConnectionString);
        dataSourceBuilder.UseVector();
        await using var dataSource = dataSourceBuilder.Build();
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

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var type = element.GetProperty("type").GetString();
            var name = element.GetProperty("name").GetString();
            
            if (type == "file" && name?.EndsWith(".md", StringComparison.OrdinalIgnoreCase) == true)
            {
                var downloadUrl = element.GetProperty("download_url").GetString();
                if (downloadUrl != null)
                {
                    logger.LogInformation("Downloading journal file {FileName}", name);
                    var fileContent = await client.GetStringAsync(downloadUrl, context.CancellationToken);
                    
                    var entries = await ParseJournalEntriesAsync(name, fileContent, context.CancellationToken);
                    foreach (var entry in entries)
                    {
                        var idString = $"{name}-{entry.Date:yyyy-MM-dd}";
                        using var md5 = System.Security.Cryptography.MD5.Create();
                        var idBytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(idString));
                        var id = new Guid(idBytes);

                        var existingContent = await GetExistingContentAsync(connection, id, context.CancellationToken);

                        if (existingContent == entry.Content)
                        {
                            logger.LogInformation("Skipping entry {Date} in {FileName} because content has not changed.", entry.Date.ToString("yyyy-MM-dd"), name);
                            continue;
                        }

                        var embeddingResponse = await _embeddingClient.GenerateEmbeddingAsync(entry.Content, cancellationToken: context.CancellationToken);
                        var embedding = embeddingResponse.Value.ToFloats().ToArray();
                        await UpsertEntryAsync(connection, id, name, entry.Date, entry.Content, embedding, context.CancellationToken);
                        entriesSynced++;
                    }
                }
            }
        }

        logger.LogInformation("Finished syncing {Count} journal entries", entriesSynced);
        await context.Publish(new WorkJournalSyncedEvent(entriesSynced, DateTime.UtcNow), context.CancellationToken);
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

    private async Task<IEnumerable<(DateTime Date, string Content)>> ParseJournalEntriesAsync(string fileName, string fileContent, CancellationToken cancellationToken)
    {
        var prompt = $$"""
        You are a helpful data extraction assistant.
        Parse the following work journal markdown file into distinct entries.
        The filename is '{{fileName}}', which indicates the year and month (e.g. 2026_01.md implies Jan 2026).
        Entries start with '## ' headings that represent dates or date ranges (e.g. '## 01/2' or '## 01/4-5').
        
        Return a JSON object with the following structure exactly:
        {
            "entries": [
                {
                    "date": "YYYY-MM-DD", // The exact start date of the entry based on the heading and filename. If it's a range, use the FIRST date.
                    "content": "The full markdown content of the entry, including the heading and bullet points."
                }
            ]
        }

        Markdown content:
        {{fileContent}}
        """;

        var response = await _chatClient.CompleteChatAsync(
            [new UserChatMessage(prompt)],
            new ChatCompletionOptions { ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat() },
            cancellationToken);

        var json = response.Value.Content[0].Text;
        using var doc = JsonDocument.Parse(json);
        var entries = new List<(DateTime Date, string Content)>();

        if (doc.RootElement.TryGetProperty("entries", out var entriesArray))
        {
            foreach (var element in entriesArray.EnumerateArray())
            {
                if (element.TryGetProperty("date", out var dateElement) && 
                    element.TryGetProperty("content", out var contentElement) &&
                    DateTime.TryParse(dateElement.GetString(), out var date))
                {
                    entries.Add((date, contentElement.GetString() ?? ""));
                }
            }
        }

        return entries;
    }
}
