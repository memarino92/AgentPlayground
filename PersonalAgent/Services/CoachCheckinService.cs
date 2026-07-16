using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using System.Security.Cryptography;
using System.Globalization;

namespace PersonalAgent.Services;

internal class CoachCheckinService(
    IBus bus,
    IOptions<SqlTransportOptions> sqlOptions,
    IOptions<AgentMemoryOptions> memoryOptions,
    IOptions<CoachCheckinOptions> coachOptions,
    IAgentEmbeddingService embeddingService,
    ILogger<CoachCheckinService> logger)
{
    private readonly string _connectionString = sqlOptions.Value.ConnectionString ?? string.Empty;
    private readonly string _schema = memoryOptions.Value.Schema;
    private readonly CoachCheckinOptions _options = coachOptions.Value;

    public async Task<CoachCallUploadResult> CreateUploadAsync(string profileId, string originalFileName, string mimeType, byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length > _options.MaxUploadMb * 1024 * 1024)
            throw new InvalidOperationException($"Upload exceeds maximum allowed size of {_options.MaxUploadMb}MB.");

        var uploadId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var duplicateUpload = await GetExistingUploadByHashAsync(connection, profileId, hash, cancellationToken);
        if (duplicateUpload is not null)
        {
            logger.LogInformation(
                "Detected duplicate coach call upload for profile {ProfileId} with existing upload {UploadId}",
                profileId.ReplaceLineEndings(""),
                duplicateUpload.UploadId);
            return new CoachCallUploadResult(
                duplicateUpload.UploadId,
                duplicateUpload.CorrelationId,
                duplicateUpload.Status,
                duplicateUpload.CreatedAtUtc,
                true);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int uploadRowsInserted;
        await using (var uploadCommand = connection.CreateCommand())
        {
            uploadCommand.Transaction = transaction;
            uploadCommand.CommandText = $"""
                INSERT INTO {CoachCallUploadsTable}
                (
                    upload_id,
                    profile_id,
                    session_id,
                    correlation_id,
                    original_file_name,
                    mime_type,
                    size_bytes,
                    file_hash,
                    audio_bytes,
                    status,
                    error,
                    created_at,
                    updated_at
                )
                VALUES
                (
                    @uploadId,
                    @profileId,
                    @sessionId,
                    @correlationId,
                    @originalFileName,
                    @mimeType,
                    @sizeBytes,
                    @fileHash,
                    @audioBytes,
                    @status,
                    NULL,
                    @createdAt,
                    @updatedAt
                )
                ON CONFLICT (profile_id, file_hash) DO NOTHING;
                """;
            uploadCommand.Parameters.AddWithValue("uploadId", uploadId);
            uploadCommand.Parameters.AddWithValue("profileId", profileId);
            uploadCommand.Parameters.AddWithValue("sessionId", sessionId);
            uploadCommand.Parameters.AddWithValue("correlationId", correlationId);
            uploadCommand.Parameters.AddWithValue("originalFileName", originalFileName);
            uploadCommand.Parameters.AddWithValue("mimeType", mimeType);
            uploadCommand.Parameters.AddWithValue("sizeBytes", bytes.LongLength);
            uploadCommand.Parameters.AddWithValue("fileHash", hash);
            uploadCommand.Parameters.AddWithValue("audioBytes", bytes);
            uploadCommand.Parameters.AddWithValue("status", CoachCallUploadStatus.Uploaded.ToString());
            uploadCommand.Parameters.AddWithValue("createdAt", createdAt);
            uploadCommand.Parameters.AddWithValue("updatedAt", createdAt);
            uploadRowsInserted = await uploadCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (uploadRowsInserted == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            var concurrentDuplicate = await GetExistingUploadByHashAsync(connection, profileId, hash, cancellationToken);
            logger.LogInformation(
                "Detected concurrent duplicate coach call upload for profile {ProfileId} with existing upload {UploadId}",
                profileId.ReplaceLineEndings(""),
                concurrentDuplicate?.UploadId);
            if (concurrentDuplicate is null)
                throw new InvalidOperationException($"Insert for upload with file hash {hash} was a no-op but no conflicting row was found for profile {profileId.ReplaceLineEndings("")}.");
            return new CoachCallUploadResult(
                concurrentDuplicate.UploadId,
                concurrentDuplicate.CorrelationId,
                concurrentDuplicate.Status,
                concurrentDuplicate.CreatedAtUtc,
                true);
        }

        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = $"""
                INSERT INTO {CoachCallSessionsTable}
                (
                    session_id,
                    upload_id,
                    profile_id,
                    transcript_text,
                    summary_markdown,
                    summary_json,
                    created_at,
                    updated_at
                )
                VALUES
                (
                    @sessionId,
                    @uploadId,
                    @profileId,
                    '',
                    '',
                    @summaryJson::jsonb,
                    @createdAt,
                    @updatedAt
                );
                """;
            sessionCommand.Parameters.AddWithValue("sessionId", sessionId);
            sessionCommand.Parameters.AddWithValue("uploadId", uploadId);
            sessionCommand.Parameters.AddWithValue("profileId", profileId);
            sessionCommand.Parameters.AddWithValue("summaryJson", "{}");
            sessionCommand.Parameters.AddWithValue("createdAt", createdAt);
            sessionCommand.Parameters.AddWithValue("updatedAt", createdAt);
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        await bus.Publish(new TranscribeCoachCallCommand(uploadId, profileId, correlationId), cancellationToken);
        logger.LogInformation("Created coach call upload {UploadId} for profile {ProfileId}", uploadId, profileId);

        return new CoachCallUploadResult(uploadId, correlationId, CoachCallUploadStatus.Uploaded, createdAt, false);
    }

    public async Task<IReadOnlyList<CoachCheckinAdminItem>> GetRecentUploadsAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT u.upload_id,
                   u.session_id,
                   u.profile_id,
                   u.original_file_name,
                   u.status,
                   u.error,
                   u.created_at,
                   u.updated_at,
                   u.audio_bytes IS NOT NULL AS has_audio_blob,
                   COALESCE(utterance_counts.utterance_count, 0) AS utterance_count,
                   COALESCE(chunk_counts.chunk_count, 0) AS chunk_count
            FROM {CoachCallUploadsTable} u
            LEFT JOIN
            (
                SELECT session_id, COUNT(*)::integer AS utterance_count
                FROM {CoachCallUtterancesTable}
                GROUP BY session_id
            ) AS utterance_counts ON utterance_counts.session_id = u.session_id
            LEFT JOIN
            (
                SELECT session_id, COUNT(*)::integer AS chunk_count
                FROM {CoachCallChunksTable}
                GROUP BY session_id
            ) AS chunk_counts ON chunk_counts.session_id = u.session_id
            ORDER BY u.created_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("limit", normalizedLimit);

        var items = new List<CoachCheckinAdminItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var uploadId = reader.GetGuid(0);
            items.Add(new CoachCheckinAdminItem(
                uploadId,
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseStatus(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetBoolean(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                await GetSpeakerLabelsAsync(connection, uploadId, cancellationToken)));
        }

        return items;
    }

    public async Task<CoachCheckinStatusResponse?> GetStatusAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT u.upload_id, u.session_id, u.profile_id, u.status, u.error, u.created_at, u.updated_at
            FROM {CoachCallUploadsTable} u
            WHERE u.upload_id = @uploadId
              AND u.profile_id = @profileId;
            """;
        command.Parameters.AddWithValue("uploadId", uploadId);
        command.Parameters.AddWithValue("profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CoachCheckinStatusResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            ParseStatus(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<CoachCheckinSummaryResponse?> GetSummaryAsync(Guid uploadId, string profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT u.upload_id, s.session_id, s.summary_markdown, s.summary_json::text, s.updated_at
            FROM {CoachCallUploadsTable} u
            JOIN {CoachCallSessionsTable} s ON s.upload_id = u.upload_id
            WHERE u.upload_id = @uploadId
              AND u.profile_id = @profileId;
            """;
        command.Parameters.AddWithValue("uploadId", uploadId);
        command.Parameters.AddWithValue("profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CoachCheckinSummaryResponse(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task ApplySpeakerOverridesAsync(Guid uploadId, string profileId, IReadOnlyList<CoachSpeakerOverrideItem> overrides, CancellationToken cancellationToken = default)
    {
        if (overrides.Count is 0) return;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        Guid sessionId;
        Guid correlationId;
        await using (var metadataCommand = connection.CreateCommand())
        {
            metadataCommand.Transaction = transaction;
            metadataCommand.CommandText = $"""
                SELECT session_id, correlation_id
                FROM {CoachCallUploadsTable}
                WHERE upload_id = @uploadId
                  AND profile_id = @profileId;
                """;
            metadataCommand.Parameters.AddWithValue("uploadId", uploadId);
            metadataCommand.Parameters.AddWithValue("profileId", profileId);
            await using var reader = await metadataCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Upload not found.");
            sessionId = reader.GetGuid(0);
            correlationId = reader.GetGuid(1);
        }

        foreach (var item in overrides)
        {
            await using var overrideCommand = connection.CreateCommand();
            overrideCommand.Transaction = transaction;
            overrideCommand.CommandText = $"""
                INSERT INTO {CoachCallSpeakerOverridesTable} (upload_id, speaker_label, speaker_role, created_at)
                VALUES (@uploadId, @speakerLabel, @speakerRole, @createdAt)
                ON CONFLICT (upload_id, speaker_label)
                DO UPDATE SET speaker_role = EXCLUDED.speaker_role, created_at = EXCLUDED.created_at;
                """;
            overrideCommand.Parameters.AddWithValue("uploadId", uploadId);
            overrideCommand.Parameters.AddWithValue("speakerLabel", item.SpeakerLabel);
            overrideCommand.Parameters.AddWithValue("speakerRole", item.Role);
            overrideCommand.Parameters.AddWithValue("createdAt", DateTimeOffset.UtcNow);
            await overrideCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var uploadCommand = connection.CreateCommand())
        {
            uploadCommand.Transaction = transaction;
            uploadCommand.CommandText = $"""
                UPDATE {CoachCallUploadsTable}
                SET status = @status,
                    error = NULL,
                    updated_at = @updatedAt
                WHERE upload_id = @uploadId;
                """;
            uploadCommand.Parameters.AddWithValue("uploadId", uploadId);
            uploadCommand.Parameters.AddWithValue("status", CoachCallUploadStatus.Processing.ToString());
            uploadCommand.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
            await uploadCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await bus.Publish(new ProcessCoachTranscriptCommand(uploadId, sessionId, profileId, correlationId), cancellationToken);
    }

    public async Task<string> SearchCoachCheckinsAsync(string query, string profileId, string? exerciseTag = null, CancellationToken cancellationToken = default)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
        var vectorLiteral = "[" + string.Join(",", embedding.ToArray().Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]";

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.upload_id, c.start_ms, c.end_ms, c.content
            FROM {CoachCallChunksTable} c
            JOIN {CoachCallSessionsTable} s ON s.session_id = c.session_id
            WHERE s.profile_id = @profileId
              AND (@exerciseTag IS NULL OR c.exercise_tags @> jsonb_build_array(@exerciseTag))
            ORDER BY c.embedding <=> @queryEmbedding::vector
            LIMIT 5;
            """;
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.Add(new NpgsqlParameter("exerciseTag", NpgsqlDbType.Text) { Value = (object?)exerciseTag ?? DBNull.Value });
        command.Parameters.AddWithValue("queryEmbedding", vectorLiteral);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add($"Upload {reader.GetGuid(0)} [{FormatTimestamp(reader.GetInt32(1))}-{FormatTimestamp(reader.GetInt32(2))}] {reader.GetString(3)}");
        }

        return lines.Count is 0
            ? "No matching coach check-in chunks found."
            : string.Join("\n\n", lines);
    }

    private static CoachCallUploadStatus ParseStatus(string value) => Enum.TryParse<CoachCallUploadStatus>(value, true, out var status)
        ? status
        : CoachCallUploadStatus.Failed;

    private async Task<List<CoachCheckinSpeakerLabelInfo>> GetSpeakerLabelsAsync(NpgsqlConnection connection, Guid uploadId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT u.speaker_label,
                   COALESCE(NULLIF(TRIM(u.speaker_role), ''), 'unknown') AS speaker_role,
                   COUNT(*)::integer AS utterance_count
            FROM {CoachCallUtterancesTable} u
            JOIN {CoachCallUploadsTable} uploads ON uploads.session_id = u.session_id
            WHERE uploads.upload_id = @uploadId
            GROUP BY u.speaker_label, COALESCE(NULLIF(TRIM(u.speaker_role), ''), 'unknown')
            ORDER BY u.speaker_label;
            """;
        command.Parameters.AddWithValue("uploadId", uploadId);

        var results = new List<CoachCheckinSpeakerLabelInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new CoachCheckinSpeakerLabelInfo(reader.GetInt32(0), reader.GetString(1), reader.GetInt32(2)));

        return results;
    }

    private async Task<CoachCallUploadResult?> GetExistingUploadByHashAsync(NpgsqlConnection connection, string profileId, string hash, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT upload_id, correlation_id, status, created_at
            FROM {CoachCallUploadsTable}
            WHERE profile_id = @profileId
              AND file_hash = @fileHash
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("profileId", profileId);
        command.Parameters.AddWithValue("fileHash", hash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new CoachCallUploadResult(
            reader.GetGuid(0),
            reader.GetGuid(1),
            ParseStatus(reader.GetString(2)),
            reader.GetFieldValue<DateTimeOffset>(3),
            true);
    }

    private Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        return OpenConnectionAsync(connection, cancellationToken);
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private string QualifiedTableName(string tableName) => $"{QuoteIdentifier(_schema)}.{QuoteIdentifier(tableName)}";
    private string CoachCallUploadsTable => QualifiedTableName("coach_call_uploads");
    private string CoachCallSessionsTable => QualifiedTableName("coach_call_sessions");
    private string CoachCallChunksTable => QualifiedTableName("coach_call_chunks");
    private string CoachCallUtterancesTable => QualifiedTableName("coach_call_utterances");
    private string CoachCallSpeakerOverridesTable => QualifiedTableName("coach_call_speaker_overrides");

    private static string FormatTimestamp(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss");
}
