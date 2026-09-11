using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Events;
using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Models;
using PersonalAgent.Worker.Services;
using System.Globalization;

namespace PersonalAgent.Worker.Consumers;

internal class ProcessCoachTranscriptConsumer(
    IOptions<SqlTransportOptions> sqlOptions,
    IOptions<CoachCheckinWorkerOptions> options,
    CoachTranscriptProcessingService processingService,
    ILogger<ProcessCoachTranscriptConsumer> logger) : IConsumer<ProcessCoachTranscriptCommand>
{
    private readonly string _connectionString = sqlOptions.Value.ConnectionString ?? string.Empty;
    private readonly CoachCheckinWorkerOptions _options = options.Value;

    public async Task Consume(ConsumeContext<ProcessCoachTranscriptCommand> context)
    {
        var message = context.Message;
        logger.LogInformation("Processing coach transcript for upload {UploadId}, session {SessionId}", message.UploadId, message.SessionId);

        try
        {
            await using var connection = await OpenConnectionAsync(context.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken);
            await using (var guard = connection.CreateCommand())
            {
                guard.CommandText = $"SELECT status FROM {CoachCallUploadsTable} WHERE upload_id = @id AND session_id = @session AND profile_id = @profile FOR UPDATE";
                guard.Parameters.AddWithValue("id", message.UploadId);
                guard.Parameters.AddWithValue("session", message.SessionId);
                guard.Parameters.AddWithValue("profile", message.ProfileId);
                if (await guard.ExecuteScalarAsync(context.CancellationToken) is not "Processing") return;
            }
            var utterances = await LoadUtterancesAsync(connection, message.SessionId, message.UploadId, context.CancellationToken);
            if (utterances.Count is 0) throw new InvalidOperationException("No transcribed utterances available.");

            await ApplySpeakerOverridesAsync(connection, message.UploadId, message.SessionId, context.CancellationToken);
            var enrichedUtterances = await LoadUtterancesAsync(connection, message.SessionId, message.UploadId, context.CancellationToken);
            var result = await processingService.ProcessAsync(message.CorrelationId, enrichedUtterances, context.CancellationToken);

            await SaveResultAsync(connection, transaction, message, result, context.CancellationToken);
            await UpdateUploadStatusAsync(connection, message.UploadId, "Completed", null, context.CancellationToken);
            await CoachCallOutbox.EnqueueAsync(transaction, _options.Schema, new CoachCallStatusChangedEvent(message.UploadId, message.ProfileId, "Completed"), context.CancellationToken);

            await CoachCallOutbox.EnqueueAsync(transaction, _options.Schema,
                new CoachCallProcessingCompletedEvent(
                    message.UploadId,
                    message.SessionId,
                    message.ProfileId,
                    message.CorrelationId,
                    result.Chunks.Count,
                    DateTimeOffset.UtcNow),
                context.CancellationToken);
            await transaction.CommitAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Coach transcript processing failed for upload {UploadId}", message.UploadId);
            throw;
        }
    }

    private async Task<List<TranscribedUtterance>> LoadUtterancesAsync(NpgsqlConnection connection, Guid sessionId, Guid uploadId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT u.speaker_label, u.speaker_role, u.start_ms, u.end_ms, u.content, u.confidence
            FROM {CoachCallUtterancesTable} u
            WHERE u.session_id = @sessionId
            ORDER BY u.start_ms;
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);

        var utterances = new List<TranscribedUtterance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            utterances.Add(new TranscribedUtterance(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetDouble(5)));

        return utterances;
    }

    private async Task ApplySpeakerOverridesAsync(NpgsqlConnection connection, Guid uploadId, Guid sessionId, CancellationToken cancellationToken)
    {
        var overrides = new Dictionary<int, string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT speaker_label, speaker_role
                FROM {CoachCallSpeakerOverridesTable}
                WHERE upload_id = @uploadId;
                """;
            command.Parameters.AddWithValue("uploadId", uploadId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                overrides[reader.GetInt32(0)] = reader.GetString(1);
        }

        if (overrides.Count is 0) return;

        foreach (var entry in overrides)
        {
            await using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = $"""
                UPDATE {CoachCallUtterancesTable}
                SET speaker_role = @speakerRole
                WHERE session_id = @sessionId
                  AND speaker_label = @speakerLabel;
                """;
            updateCommand.Parameters.AddWithValue("sessionId", sessionId);
            updateCommand.Parameters.AddWithValue("speakerLabel", entry.Key);
            updateCommand.Parameters.AddWithValue("speakerRole", entry.Value);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task SaveResultAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ProcessCoachTranscriptCommand message, CoachTranscriptProcessingResult result, CancellationToken cancellationToken)
    {
        await using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.Transaction = transaction;
            sessionCommand.CommandText = $"""
                UPDATE {CoachCallSessionsTable}
                SET summary_markdown = @summaryMarkdown,
                    summary_json = @summaryJson::jsonb,
                    updated_at = @updatedAt
                WHERE session_id = @sessionId;
                """;
            sessionCommand.Parameters.AddWithValue("sessionId", message.SessionId);
            sessionCommand.Parameters.AddWithValue("summaryMarkdown", result.SummaryMarkdown);
            sessionCommand.Parameters.AddWithValue("summaryJson", result.SummaryJson);
            sessionCommand.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = $"DELETE FROM {CoachCallChunksTable} WHERE session_id = @sessionId;";
            deleteCommand.Parameters.AddWithValue("sessionId", message.SessionId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in result.Chunks)
        {
            await using var chunkCommand = connection.CreateCommand();
            chunkCommand.Transaction = transaction;
            chunkCommand.CommandText = $"""
                INSERT INTO {CoachCallChunksTable}
                (
                    session_id,
                    chunk_index,
                    start_ms,
                    end_ms,
                    content,
                    speaker_mix,
                    exercise_tags,
                    intent_tags,
                    priority_tags,
                    metadata,
                    embedding,
                    created_at
                )
                VALUES
                (
                    @sessionId,
                    @chunkIndex,
                    @startMs,
                    @endMs,
                    @content,
                    @speakerMix,
                    @exerciseTags::jsonb,
                    @intentTags::jsonb,
                    @priorityTags::jsonb,
                    @metadata::jsonb,
                    @embedding::vector,
                    @createdAt
                );
                """;
            chunkCommand.Parameters.AddWithValue("sessionId", message.SessionId);
            chunkCommand.Parameters.AddWithValue("chunkIndex", chunk.ChunkIndex);
            chunkCommand.Parameters.AddWithValue("startMs", chunk.StartMs);
            chunkCommand.Parameters.AddWithValue("endMs", chunk.EndMs);
            chunkCommand.Parameters.AddWithValue("content", chunk.Content);
            chunkCommand.Parameters.AddWithValue("speakerMix", chunk.SpeakerMix);
            chunkCommand.Parameters.AddWithValue("exerciseTags", chunk.ExerciseTags);
            chunkCommand.Parameters.AddWithValue("intentTags", chunk.IntentTags);
            chunkCommand.Parameters.AddWithValue("priorityTags", chunk.PriorityTags);
            chunkCommand.Parameters.AddWithValue("metadata", chunk.MetadataJson);
            chunkCommand.Parameters.AddWithValue("embedding", ToVectorLiteral(chunk.Embedding));
            chunkCommand.Parameters.AddWithValue("createdAt", DateTimeOffset.UtcNow);
            await chunkCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task UpdateUploadStatusAsync(NpgsqlConnection connection, Guid uploadId, string status, string? error, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {CoachCallUploadsTable}
            SET status = @status,
                error = @error,
                updated_at = @updatedAt
            WHERE upload_id = @uploadId;
            """;
        command.Parameters.AddWithValue("uploadId", uploadId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private string CoachCallUploadsTable => QualifiedTableName("coach_call_uploads");
    private string CoachCallUtterancesTable => QualifiedTableName("coach_call_utterances");
    private string CoachCallSessionsTable => QualifiedTableName("coach_call_sessions");
    private string CoachCallChunksTable => QualifiedTableName("coach_call_chunks");
    private string CoachCallSpeakerOverridesTable => QualifiedTableName("coach_call_speaker_overrides");

    private string QualifiedTableName(string tableName) => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(tableName)}";
    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
    private static string ToVectorLiteral(float[] values) => "[" + string.Join(",", values.Select(value => value.ToString(CultureInfo.InvariantCulture))) + "]";
}
