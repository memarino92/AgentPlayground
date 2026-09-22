using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PersonalAgent.Contracts;
using Npgsql;
using NpgsqlTypes;
using PersonalAgent.Api.Models;
using Pgvector;

namespace PersonalAgent.Api.Services;

internal partial class PostgresAgentSessionStore
{
    public async Task<IAsyncDisposable> LockConversationAsync(Guid Id, CancellationToken Token)
    {
        var Connection = await OpenConnectionAsync(Token);
        try
        {
            var Transaction = await Connection.BeginTransactionAsync(Token);
            await using var Command = Connection.CreateCommand();
            Command.Transaction = Transaction;
            Command.CommandText = "SELECT pg_advisory_xact_lock(@key)";
            Command.Parameters.AddWithValue("key", BitConverter.ToInt64(Id.ToByteArray()));
            await Command.ExecuteNonQueryAsync(Token);
            return new ConversationLock(Connection, Transaction);
        }
        catch { await Connection.DisposeAsync(); throw; }
    }

    public static Guid ConversationId(AgentAccessContext Access)
    {
        var Scope = JsonSerializer.Serialize(new[] { "continuous-v1", Access.ActorId.ToLowerInvariant(), Access.Role.ToLowerInvariant(), Access.SubjectProfileId.ToLowerInvariant() });
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(Scope)).AsSpan(0, 16));
    }

    public async Task<Guid> EnsureConversationAsync(AgentAccessContext Access, string ModelId, CancellationToken Token)
    {
        var Id = ConversationId(Access);
        await using var Guard = await LockConversationAsync(Id, Token);
        if (await GetSessionAsync(Id, Token) is null)
            await CreateSessionAsync(Id, Access, JsonSerializer.Serialize(new AgentSessionState(ModelId) { IsContinuous = true }), Token);
        return Id;
    }

    public async Task<List<ConversationMessage>> GetRecentMessagesAsync(Guid Id, long AfterSequence, int Limit, CancellationToken Token)
    {
        await using var Connection = await OpenConnectionAsync(Token);
        await using var Command = Connection.CreateCommand();
        Command.CommandText = $"""
            SELECT role, content, message_seq, created_at, metadata FROM (
                SELECT role, content, message_seq, created_at, metadata FROM {TranscriptMessagesTable}
                WHERE session_id = @id AND message_seq > @after
                ORDER BY message_seq DESC LIMIT @limit
            ) recent ORDER BY message_seq;
            """;
        Command.Parameters.AddWithValue("id", Id);
        Command.Parameters.AddWithValue("after", AfterSequence);
        Command.Parameters.AddWithValue("limit", Math.Clamp(Limit, 1, 100));
        var Messages = new List<ConversationMessage>();
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        while (await Reader.ReadAsync(Token)) Messages.Add(ReadConversationMessage(Reader));
        return Messages;
    }

    private static ConversationMessage ReadConversationMessage(NpgsqlDataReader Reader) => new(Reader.GetString(0), Reader.GetString(1))
    {
        Sequence = Reader.GetInt64(2), CreatedAt = Reader.GetFieldValue<DateTimeOffset>(3),
        Presentation = Reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<ChatPresentation>(Reader.GetString(4))
    };

    public async Task<List<HistoricalTurn>> SearchHistoryAsync(AgentAccessContext Access, Guid CurrentId, long BeforeSequence,
        DateTimeOffset? After, string Query, ReadOnlyMemory<float>? Embedding, CancellationToken Token)
    {
        await using var Connection = await OpenConnectionAsync(Token);
        await using var Command = Connection.CreateCommand();
        var Semantic = Embedding is not null && _options.EnableSemanticMemory ? $"""
            UNION ALL
            SELECT t.message_id, (1 - (m.embedding <=> @embedding))::real AS rank
            FROM {MemoryRecordsTable} m
            JOIN {TranscriptMessagesTable} t ON t.session_id = m.session_id AND t.role = 'user'
                AND (m.source_message_id = t.message_id OR (m.source_message_id IS NULL AND m.content = t.content))
            JOIN {SessionsTable} s ON s.session_id = t.session_id
            WHERE lower(s.actor_id) = @actor AND lower(s.profile_id) = @subject AND lower(s.role_name) = @role
                AND (s.session_state ->> 'ScheduledTaskId') IS NULL
                AND m.memory_kind IN ('user', 'conversation') AND m.embedding IS NOT NULL
                AND m.embedding_model = @model AND (m.embedding <=> @embedding) <= 0.45
                AND (@after IS NULL OR t.created_at > @after)
                AND (t.session_id <> @id OR t.message_seq < @before)
            """ : string.Empty;
        Command.CommandText = $"""
            WITH candidates AS (
                SELECT t.message_id, ts_rank_cd(to_tsvector('english', t.content), websearch_to_tsquery('english', @query)) AS rank
                FROM {TranscriptMessagesTable} t JOIN {SessionsTable} s ON s.session_id = t.session_id
                WHERE lower(s.actor_id) = @actor AND lower(s.profile_id) = @subject AND lower(s.role_name) = @role
                    AND (s.session_state ->> 'ScheduledTaskId') IS NULL AND t.role = 'user'
                    AND (@after IS NULL OR t.created_at > @after)
                    AND (t.session_id <> @id OR t.message_seq < @before)
                    AND to_tsvector('english', t.content) @@ websearch_to_tsquery('english', @query)
                {Semantic}
            ), ranked AS (SELECT message_id, max(rank) AS rank FROM candidates GROUP BY message_id)
            SELECT t.session_id, t.message_seq, t.created_at, left(t.content, 2000), left(COALESCE(a.content, ''), 2000)
            FROM ranked r JOIN {TranscriptMessagesTable} t ON t.message_id = r.message_id
            LEFT JOIN {TranscriptMessagesTable} a ON a.session_id = t.session_id AND a.message_seq = t.message_seq + 1 AND a.role = 'assistant'
            ORDER BY r.rank DESC, t.created_at DESC LIMIT 5;
            """;
        Command.Parameters.AddWithValue("actor", Access.ActorId.ToLowerInvariant());
        Command.Parameters.AddWithValue("subject", Access.SubjectProfileId.ToLowerInvariant());
        Command.Parameters.AddWithValue("role", Access.Role.ToLowerInvariant());
        Command.Parameters.AddWithValue("id", CurrentId);
        Command.Parameters.AddWithValue("before", BeforeSequence);
        Command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.TimestampTz) { Value = After ?? (object)DBNull.Value });
        var Terms = System.Text.RegularExpressions.Regex.Matches(Query, @"[\p{L}\p{N}]{3,}")
            .Select(Match => Match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(16);
        Command.Parameters.AddWithValue("query", string.Join(" OR ", Terms));
        if (!string.IsNullOrEmpty(Semantic))
        {
            Command.Parameters.AddWithValue("embedding", new Vector(Embedding!.Value.ToArray()));
            Command.Parameters.AddWithValue("model", _options.EmbeddingModel);
        }
        var Turns = new List<HistoricalTurn>();
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        while (await Reader.ReadAsync(Token))
        {
            var User = Reader.GetString(3);
            Turns.Add(new(new(Reader.GetGuid(0), Reader.GetInt64(1), Reader.GetFieldValue<DateTimeOffset>(2), User[..Math.Min(160, User.Length)]), User, Reader.GetString(4)));
        }
        return Turns;
    }

    public async Task IndexTurnAsync(Guid Id, long Sequence, string ProfileId, string Content, ReadOnlyMemory<float> Embedding, CancellationToken Token)
    {
        if (!_options.EnableSemanticMemory) return;
        await using var Connection = await OpenConnectionAsync(Token);
        await using var Command = Connection.CreateCommand();
        Command.CommandText = $"""
            INSERT INTO {MemoryRecordsTable} (session_id, profile_id, source_message_id, memory_kind, content, embedding_model, embedding, created_at, updated_at)
            SELECT session_id, @profile, message_id, 'conversation', @content, @model, @embedding, created_at, now()
            FROM {TranscriptMessagesTable} t WHERE session_id = @id AND message_seq = @seq
                AND NOT EXISTS (SELECT 1 FROM {MemoryRecordsTable} m WHERE m.source_message_id = t.message_id AND m.memory_kind = 'conversation');
            """;
        Command.Parameters.AddWithValue("profile", ProfileId);
        Command.Parameters.AddWithValue("content", Content);
        Command.Parameters.AddWithValue("model", _options.EmbeddingModel);
        Command.Parameters.AddWithValue("embedding", new Vector(Embedding.ToArray()));
        Command.Parameters.AddWithValue("id", Id);
        Command.Parameters.AddWithValue("seq", Sequence);
        await Command.ExecuteNonQueryAsync(Token);
    }

    public Task<bool> SavePresentedInteractionAsync(Guid Id, string User, string Assistant, string State, ChatPresentation Presentation, CancellationToken Token) =>
        SaveInteractionCoreAsync(Id, User, Assistant, State, Presentation, Token);

    public async Task<ChatCard?> UpdateCardAsync(Guid Id, long Sequence, string CardId, ChatCardAction Action, CancellationToken Token)
    {
        await using var Connection = await OpenConnectionAsync(Token);
        await using var Transaction = await Connection.BeginTransactionAsync(Token);
        await using var Command = Connection.CreateCommand();
        Command.Transaction = Transaction;
        Command.CommandText = $"SELECT metadata FROM {TranscriptMessagesTable} WHERE session_id = @id AND message_seq = @seq AND role = 'assistant' FOR UPDATE";
        Command.Parameters.AddWithValue("id", Id);
        Command.Parameters.AddWithValue("seq", Sequence);
        var Json = await Command.ExecuteScalarAsync(Token) as string;
        var Presentation = Json is null ? null : JsonSerializer.Deserialize<ChatPresentation>(Json);
        var Card = Presentation?.Cards.SingleOrDefault(Value => Value.Id == CardId);
        var Updated = Card is null ? null : ChatCardParser.Apply(Card, Action);
        if (Updated is null) return null;
        Command.CommandText = $"UPDATE {TranscriptMessagesTable} SET metadata = @metadata::jsonb WHERE session_id = @id AND message_seq = @seq";
        Command.Parameters.AddWithValue("metadata", JsonSerializer.Serialize(Presentation! with
        { Cards = Presentation!.Cards.Select(Value => Value.Id == CardId ? Updated : Value).ToList() }));
        await Command.ExecuteNonQueryAsync(Token);
        await Transaction.CommitAsync(Token);
        return Updated;
    }

    private sealed class ConversationLock(NpgsqlConnection Connection, NpgsqlTransaction Transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await Transaction.DisposeAsync(); }
            finally { await Connection.DisposeAsync(); }
        }
    }
}
