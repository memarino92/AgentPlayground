using AgentPlayground.Contracts.Commands;
using AgentPlayground.Contracts.Events;
using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Models;
using PersonalAgent.Worker.Services;

namespace PersonalAgent.Worker.Consumers;

internal class TranscribeCoachCallConsumer(
    IOptions<SqlTransportOptions> sqlOptions,
    IOptions<CoachCheckinWorkerOptions> options,
    ITranscriptionService transcriptionService,
    ILogger<TranscribeCoachCallConsumer> logger) : IConsumer<TranscribeCoachCallCommand>
{
    private readonly string _connectionString = sqlOptions.Value.ConnectionString ?? string.Empty;
    private readonly CoachCheckinWorkerOptions _options = options.Value;

    public async Task Consume(ConsumeContext<TranscribeCoachCallCommand> context)
    {
        var message = context.Message;
        logger.LogInformation("Starting coach call transcription for upload {UploadId}", message.UploadId);

        try
        {
            await using var connection = await OpenConnectionAsync(context.CancellationToken);
            var staged = await GetUploadAsync(connection, message.UploadId, message.ProfileId, context.CancellationToken);
            if (staged is null)
            {
                logger.LogWarning("Upload {UploadId} not found for profile {ProfileId}", message.UploadId, message.ProfileId);
                return;
            }
            await using (var claim = connection.CreateCommand())
            {
                claim.CommandText = $"UPDATE {CoachCallUploadsTable} SET status = 'Transcribing', updated_at = now() WHERE upload_id = @id AND profile_id = @profile AND status IN ('Uploaded', 'Queued', 'Transcribing')";
                claim.Parameters.AddWithValue("id", staged.UploadId);
                claim.Parameters.AddWithValue("profile", staged.ProfileId);
                if (await claim.ExecuteNonQueryAsync(context.CancellationToken) == 0) return;
            }
            var utterances = await transcriptionService.TranscribeAsync(staged.UploadId, staged.ProfileId, context.CancellationToken);
            if (utterances.Count is 0)
                throw new TranscriptionFailedException("Transcription completed without utterances.");

            await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken);
            staged = await GetUploadAsync(connection, message.UploadId, message.ProfileId, context.CancellationToken, true);
            if (staged is null) return;

            await PersistUtterancesAsync(connection, staged.SessionId, utterances, context.CancellationToken);
            await UpdateSessionTranscriptAsync(connection, staged.SessionId, utterances, context.CancellationToken);

            var requiresOverride = utterances.Select(utterance => utterance.SpeakerLabel).Distinct().Count() > 1;
            await UpdateUploadStatusAsync(connection, staged.UploadId, requiresOverride ? "AwaitingSpeakerOverride" : "Processing", null, context.CancellationToken);
            if (requiresOverride)
                logger.LogInformation("Upload {UploadId} awaiting speaker override before processing", staged.UploadId);
            else
                await CoachCallOutbox.EnqueueAsync(transaction, _options.Schema, new ProcessCoachTranscriptCommand(staged.UploadId, staged.SessionId, staged.ProfileId, staged.CorrelationId), context.CancellationToken);

            await CoachCallOutbox.EnqueueAsync(transaction, _options.Schema,
                new CoachCallTranscriptionCompletedEvent(
                    staged.UploadId,
                    staged.SessionId,
                    staged.ProfileId,
                    staged.CorrelationId,
                    utterances.Count,
                    DateTimeOffset.UtcNow),
                context.CancellationToken);
            await transaction.CommitAsync(context.CancellationToken);
        }
        catch (TranscriptionFailedException ex)
        {
            await using var connection = await OpenConnectionAsync(context.CancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken);
            var staged = await GetUploadAsync(connection, message.UploadId, message.ProfileId, context.CancellationToken, true);
            if (staged is null) return;
            await UpdateUploadStatusAsync(connection, staged.UploadId, "Failed", ex.Message, context.CancellationToken);
            await CoachCallOutbox.EnqueueAsync(transaction, _options.Schema,
                new CoachCallProcessingFailedEvent(staged.UploadId, staged.SessionId, staged.ProfileId, staged.CorrelationId, "transcription", ex.Message, DateTimeOffset.UtcNow), context.CancellationToken);
            await transaction.CommitAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Coach call transcription interrupted for upload {UploadId}; delivery can retry", message.UploadId);
            throw;
        }
    }

    private async Task<StagedCoachUpload?> GetUploadAsync(NpgsqlConnection connection, Guid uploadId, string profileId, CancellationToken cancellationToken, bool lockUpload = false)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT upload_id, session_id, profile_id, correlation_id, status
            FROM {CoachCallUploadsTable}
            WHERE upload_id = @uploadId
              AND profile_id = @profileId
            {(lockUpload ? "FOR UPDATE" : "")};
            """;
        command.Parameters.AddWithValue("uploadId", uploadId);
        command.Parameters.AddWithValue("profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        if (reader.GetString(4) is not ("Queued" or "Uploaded" or "Transcribing")) return null;
        return new StagedCoachUpload(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetGuid(3));
    }

    private async Task PersistUtterancesAsync(NpgsqlConnection connection, Guid sessionId, IReadOnlyList<TranscribedUtterance> utterances, CancellationToken cancellationToken)
    {
        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.CommandText = $"DELETE FROM {CoachCallUtterancesTable} WHERE session_id = @sessionId;";
            deleteCommand.Parameters.AddWithValue("sessionId", sessionId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var utterance in utterances)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO {CoachCallUtterancesTable}
                (
                    session_id,
                    speaker_label,
                    speaker_role,
                    start_ms,
                    end_ms,
                    confidence,
                    content
                )
                VALUES
                (
                    @sessionId,
                    @speakerLabel,
                    @speakerRole,
                    @startMs,
                    @endMs,
                    @confidence,
                    @content
                );
                """;
            command.Parameters.AddWithValue("sessionId", sessionId);
            command.Parameters.AddWithValue("speakerLabel", utterance.SpeakerLabel);
            command.Parameters.AddWithValue("speakerRole", utterance.SpeakerRole);
            command.Parameters.AddWithValue("startMs", utterance.StartMs);
            command.Parameters.AddWithValue("endMs", utterance.EndMs);
            command.Parameters.AddWithValue("confidence", utterance.Confidence);
            command.Parameters.AddWithValue("content", utterance.Text);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task UpdateSessionTranscriptAsync(NpgsqlConnection connection, Guid sessionId, IReadOnlyList<TranscribedUtterance> utterances, CancellationToken cancellationToken)
    {
        var transcript = string.Join("\n", utterances.Select(utterance => $"[{utterance.SpeakerRole}] {utterance.Text}"));
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {CoachCallSessionsTable}
            SET transcript_text = @transcript,
                updated_at = @updatedAt
            WHERE session_id = @sessionId;
            """;
        command.Parameters.AddWithValue("sessionId", sessionId);
        command.Parameters.AddWithValue("transcript", transcript);
        command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private string QualifiedTableName(string tableName) => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(tableName)}";
    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
}
