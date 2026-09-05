using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using Pgvector;
using Pgvector.Npgsql;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Services;

internal class PostgresAgentSessionStore(IOptions<AgentMemoryOptions> options, ILogger<PostgresAgentSessionStore> logger) : IAgentSessionStore, IAgentSemanticMemoryStore, IAgentApprovalStore
{
    private const int SessionStateVersion = 1;
    private readonly AgentMemoryOptions _options = options.Value;
    private NpgsqlDataSource? _dataSource;

    public async Task CreateSessionAsync(Guid sessionId, string profileId, string sessionStateJson, CancellationToken cancellationToken = default)
    {
        await CreateSessionAsync(sessionId, new AgentAccessContext(profileId, AgentRoles.Owner, profileId), sessionStateJson, cancellationToken);
    }

    public async Task CreateSessionAsync(Guid sessionId, AgentAccessContext access, string sessionStateJson, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {SessionsTable}
            (
                session_id,
                profile_id,
                actor_id,
                role_name,
                memory_profile_id,
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
                @profileId,
                @actorId,
                @roleName,
                @memoryProfileId,
                @sessionState::jsonb,
                @sessionStateVersion,
                0,
                @createdAt,
                @updatedAt,
                NULL
            );
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("profileId", access.SubjectProfileId);
        command.Parameters.AddWithValue("actorId", access.ActorId);
        command.Parameters.AddWithValue("roleName", access.Role);
        command.Parameters.AddWithValue("memoryProfileId", access.MemoryProfileId);
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
            SELECT profile_id, session_state, last_message_seq, actor_id, role_name, memory_profile_id
            FROM {SessionsTable}
            WHERE session_id = @sessionId;
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new PersistedAgentSession(
            sessionId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5));
    }

    public async Task<IReadOnlyList<PersistedAgentSessionSummary>> GetSessionsAsync(string profileId, DateTimeOffset? beforeActivityAt, Guid? beforeSessionId, int pageSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.session_id,
                   COALESCE(first_user_message.content, 'New chat') AS snippet,
                   COALESCE(s.last_message_at, s.created_at) AS last_activity_at,
                   s.created_at
            FROM {SessionsTable} s
            LEFT JOIN LATERAL
            (
                SELECT tm.content
                FROM {TranscriptMessagesTable} tm
                WHERE tm.session_id = s.session_id
                  AND tm.role = 'user'
                ORDER BY tm.message_seq ASC
                LIMIT 1
            ) AS first_user_message ON TRUE
            WHERE s.actor_id = @profileId
              AND (
                    @beforeActivityAt IS NULL
                 OR @beforeSessionId IS NULL
                 OR COALESCE(s.last_message_at, s.created_at) < @beforeActivityAt
                 OR (COALESCE(s.last_message_at, s.created_at) = @beforeActivityAt AND s.session_id < @beforeSessionId)
              )
            ORDER BY COALESCE(s.last_message_at, s.created_at) DESC, s.session_id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.Add(new NpgsqlParameter("beforeActivityAt", NpgsqlDbType.TimestampTz)
        {
            Value = beforeActivityAt ?? (object)DBNull.Value
        });
        command.Parameters.Add(new NpgsqlParameter("beforeSessionId", NpgsqlDbType.Uuid)
        {
            Value = beforeSessionId ?? (object)DBNull.Value
        });
        command.Parameters.AddWithValue("limit", pageSize);

        var sessions = new List<PersistedAgentSessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessions.Add(new PersistedAgentSessionSummary(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));

        return sessions;
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

        if (messages.Count > 0) return messages;

        await reader.DisposeAsync();

        await using var existsCommand = connection.CreateCommand();
        existsCommand.CommandText = $"SELECT 1 FROM {SessionsTable} WHERE session_id = @sessionId;";
        existsCommand.Parameters.AddWithValue("sessionId", sessionId);
        var exists = await existsCommand.ExecuteScalarAsync(cancellationToken) is not null;

        if (!exists)
        {
            logger.LogDebug("Session {SessionId} not found while loading transcript", sessionId);
            return null;
        }

        return messages;
    }

    public async Task AddMemoryAsync(Guid sessionId, string profileId, string memoryKind, string content, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {MemoryRecordsTable}
            (
                session_id,
                profile_id,
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
                @profileId,
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
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.AddWithValue("memoryKind", memoryKind);
        command.Parameters.AddWithValue("content", content);
        command.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlDbType.Jsonb) { Value = DBNull.Value });
        command.Parameters.AddWithValue("embeddingModel", _options.EmbeddingModel);
        command.Parameters.AddWithValue("embedding", new Vector(embedding.ToArray()));
        command.Parameters.AddWithValue("createdAt", now);
        command.Parameters.AddWithValue("updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<MemoryRecord>> SearchMemoriesAsync(string profileId, ReadOnlyMemory<float> embedding, int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT memory_id, content, embedding <=> @embedding AS distance
            FROM {MemoryRecordsTable}
            WHERE profile_id = @profileId
              AND memory_kind = 'user'
              AND embedding IS NOT NULL
            ORDER BY embedding <=> @embedding ASC, created_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.AddWithValue("embedding", new Vector(embedding.ToArray()));
        command.Parameters.AddWithValue("limit", limit);

        var memories = new List<MemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            memories.Add(new MemoryRecord(reader.GetInt64(0), reader.GetString(1), reader.GetDouble(2)));

        return memories;
    }

    public async Task RegisterMobileDeviceTokenAsync(string profileId, string deviceId, string platform, string pushToken, string? appVersion, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {MobileDeviceTokensTable}
            (
                profile_id,
                device_id,
                platform,
                push_token,
                app_version,
                registered_at,
                last_seen_at
            )
            VALUES
            (
                @profileId,
                @deviceId,
                @platform,
                @pushToken,
                @appVersion,
                @registeredAt,
                @lastSeenAt
            )
            ON CONFLICT (profile_id, device_id)
            DO UPDATE SET
                platform = EXCLUDED.platform,
                push_token = EXCLUDED.push_token,
                app_version = EXCLUDED.app_version,
                last_seen_at = EXCLUDED.last_seen_at;
            """;
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.AddWithValue("deviceId", deviceId);
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("pushToken", pushToken);
        command.Parameters.Add(new NpgsqlParameter("appVersion", NpgsqlDbType.Text) { Value = (object?)appVersion ?? DBNull.Value });
        command.Parameters.AddWithValue("registeredAt", now);
        command.Parameters.AddWithValue("lastSeenAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PersistedMobileDeviceToken>> GetMobileDeviceTokensAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT device_id, platform, push_token, app_version, registered_at, last_seen_at
            FROM {MobileDeviceTokensTable}
            WHERE profile_id = @profileId
            ORDER BY last_seen_at DESC;
            """;
        command.Parameters.AddWithValue("profileId", profileId);

        var tokens = new List<PersistedMobileDeviceToken>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tokens.Add(new PersistedMobileDeviceToken(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5)));

        return tokens;
    }

    public async Task<PersistedAgentApproval> CreateAgentApprovalAsync(string profileId, string sessionId, string toolName, string actionSummary, string requestedBy, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var approvalId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {AgentApprovalsTable}
            (
                approval_id,
                profile_id,
                session_id,
                tool_name,
                action_summary,
                requested_by,
                requested_at,
                expires_at,
                status,
                decision_at,
                decided_by,
                reason
            )
            VALUES
            (
                @approvalId,
                @profileId,
                @sessionId,
                @toolName,
                @actionSummary,
                @requestedBy,
                @requestedAt,
                @expiresAt,
                'pending',
                NULL,
                NULL,
                NULL
            );
            """;
        command.Parameters.AddWithValue("approvalId", approvalId);
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("toolName", toolName);
        command.Parameters.AddWithValue("actionSummary", actionSummary);
        command.Parameters.AddWithValue("requestedBy", requestedBy);
        command.Parameters.AddWithValue("requestedAt", requestedAt);
        command.Parameters.AddWithValue("expiresAt", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new PersistedAgentApproval(
            approvalId,
            profileId,
            sessionId,
            toolName,
            actionSummary,
            requestedBy,
            requestedAt,
            expiresAt,
            "pending",
            null,
            null,
            null);
    }

    public async Task<PersistedAgentApproval?> CompleteAgentApprovalAsync(Guid approvalId, string profileId, bool approved, string decidedBy, string? reason, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var status = approved ? "approved" : "denied";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {AgentApprovalsTable}
            SET status = @status,
                decision_at = @decisionAt,
                decided_by = @decidedBy,
                reason = @reason
            WHERE approval_id = @approvalId
              AND profile_id = @profileId
              AND status = 'pending'
            RETURNING approval_id, profile_id, session_id, tool_name, action_summary, requested_by, requested_at, expires_at, status, decision_at, decided_by, reason;
            """;
        command.Parameters.AddWithValue("approvalId", approvalId);
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("decisionAt", now);
        command.Parameters.AddWithValue("decidedBy", decidedBy);
        command.Parameters.Add(new NpgsqlParameter("reason", NpgsqlDbType.Text) { Value = (object?)reason ?? DBNull.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new PersistedAgentApproval(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    public async Task<PersistedAgentApproval?> GetAgentApprovalAsync(Guid approvalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT approval_id, profile_id, session_id, tool_name, action_summary, requested_by, requested_at, expires_at, status, decision_at, decided_by, reason
            FROM {AgentApprovalsTable}
            WHERE approval_id = @approvalId;
            """;
        command.Parameters.AddWithValue("approvalId", approvalId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new PersistedAgentApproval(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
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
    private string MobileDeviceTokensTable => QualifiedTableName(_options.MobileDeviceTokensTableName);
    private string AgentApprovalsTable => QualifiedTableName(_options.AgentApprovalsTableName);

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
