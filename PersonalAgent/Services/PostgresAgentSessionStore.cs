using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using Pgvector;
using Pgvector.Npgsql;

namespace PersonalAgent.Services;

internal class PostgresAgentSessionStore(IOptions<AgentMemoryOptions> options) : IAgentSessionStore, IAgentSemanticMemoryStore
{
    private const int SessionStateVersion = 1;
    private readonly AgentMemoryOptions _options = options.Value;
    private NpgsqlDataSource? _dataSource;

    public async Task CreateSessionAsync(Guid sessionId, string sessionStateJson, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {SessionsTable}
            (
                session_id,
                session_state,
                session_state_version,
                last_message_seq,
                created_at,
                updated_at,
                last_message_at
            )
            VALUES
            (
                @sessionId,
                @sessionState::jsonb,
                @sessionStateVersion,
                0,
                @createdAt,
                @updatedAt,
                NULL
            );
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("sessionState", sessionStateJson);
        command.Parameters.AddWithValue("sessionStateVersion", SessionStateVersion);
        command.Parameters.AddWithValue("createdAt", now);
        command.Parameters.AddWithValue("updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersistedAgentSession?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT session_state, last_message_seq
            FROM {SessionsTable}
            WHERE session_id = @sessionId;
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new PersistedAgentSession(sessionId, reader.GetString(0), reader.GetInt64(1));
    }

    public async Task<bool> SessionExistsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {SessionsTable} WHERE session_id = @sessionId;";
        command.Parameters.AddWithValue("sessionId", sessionId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task<bool> SaveInteractionAsync(Guid sessionId, string userMessage, string assistantMessage, string sessionStateJson, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var loadCommand = connection.CreateCommand();
        loadCommand.Transaction = transaction;
        loadCommand.CommandText = $"""
            SELECT last_message_seq
            FROM {SessionsTable}
            WHERE session_id = @sessionId
            FOR UPDATE;
            """;
        loadCommand.Parameters.AddWithValue("sessionId", sessionId);

        var currentSequence = await loadCommand.ExecuteScalarAsync(cancellationToken);
        if (currentSequence is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var nextSequence = Convert.ToInt64(currentSequence) + 1;

        await InsertMessageAsync(connection, transaction, sessionId, nextSequence, "user", userMessage, now, cancellationToken);
        await InsertMessageAsync(connection, transaction, sessionId, nextSequence + 1, "assistant", assistantMessage, now, cancellationToken);

        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = $"""
            UPDATE {SessionsTable}
            SET session_state = @sessionState::jsonb,
                session_state_version = @sessionStateVersion,
                last_message_seq = @lastMessageSequence,
                updated_at = @updatedAt,
                last_message_at = @lastMessageAt
            WHERE session_id = @sessionId;
            """;
        updateCommand.Parameters.AddWithValue("sessionId", sessionId);
        updateCommand.Parameters.AddWithValue("sessionState", sessionStateJson);
        updateCommand.Parameters.AddWithValue("sessionStateVersion", SessionStateVersion);
        updateCommand.Parameters.AddWithValue("lastMessageSequence", nextSequence + 1);
        updateCommand.Parameters.AddWithValue("updatedAt", now);
        updateCommand.Parameters.AddWithValue("lastMessageAt", now);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<List<ConversationMessage>?> GetSessionMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        if (!await SessionExistsAsync(sessionId, cancellationToken)) return null;

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT role, content
            FROM {TranscriptMessagesTable}
            WHERE session_id = @sessionId
            ORDER BY message_seq ASC;
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);

        var messages = new List<ConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            messages.Add(new ConversationMessage(reader.GetString(0), reader.GetString(1)));

        return messages;
    }

    public async Task AddMemoryAsync(Guid sessionId, string memoryKind, string content, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {MemoryRecordsTable}
            (
                session_id,
                source_message_id,
                memory_kind,
                content,
                metadata,
                embedding_model,
                embedding,
                created_at,
                updated_at
            )
            VALUES
            (
                @sessionId,
                NULL,
                @memoryKind,
                @content,
                @metadata,
                @embeddingModel,
                @embedding,
                @createdAt,
                @updatedAt
            );
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("memoryKind", memoryKind);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = DBNull.Value });
        command.Parameters.AddWithValue("embeddingModel", _options.EmbeddingModel);
        command.Parameters.AddWithValue("embedding", new Vector(embedding.ToArray()));
        command.Parameters.AddWithValue("createdAt", now);
        command.Parameters.AddWithValue("updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<MemoryRecord>> SearchMemoriesAsync(Guid sessionId, ReadOnlyMemory<float> embedding, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT memory_id, content, embedding <=> @embedding AS distance
            FROM {MemoryRecordsTable}
            WHERE session_id = @sessionId
              AND embedding IS NOT NULL
            ORDER BY embedding <=> @embedding ASC, created_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("embedding", new Vector(embedding.ToArray()));
        command.Parameters.AddWithValue("limit", limit);

        var memories = new List<MemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            memories.Add(new MemoryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));

        return memories;
    }

    private async Task InsertMessageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sessionId, long messageSequence, string role, string content, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {TranscriptMessagesTable}
            (
                session_id,
                message_seq,
                role,
                content,
                metadata,
                created_at
            )
            VALUES
            (
                @sessionId,
                @messageSequence,
                @role,
                @content,
                @metadata,
                @createdAt
            );
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("messageSequence", messageSequence);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = DBNull.Value });
        command.Parameters.AddWithValue("createdAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        _dataSource ??= BuildDataSource();
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    private string SessionsTable => QualifiedTableName(_options.SessionsTableName);
    private string TranscriptMessagesTable => QualifiedTableName(_options.TranscriptMessagesTableName);
    private string MemoryRecordsTable => QualifiedTableName(_options.MemoryRecordsTableName);

    private string QualifiedTableName(string tableName) => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(tableName)}";

    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);

    private NpgsqlDataSource BuildDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(_options.ConnectionString);
        if (_options.EnableSemanticMemory)
            builder.UseVector();

        return builder.Build();
    }
}
