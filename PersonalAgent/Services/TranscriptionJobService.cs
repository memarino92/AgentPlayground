using System.Text.Json;
using AgentPlayground.Contracts.Messaging.Responses;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal class TranscriptionJobService(
    IOptions<AgentMemoryOptions> MemoryOptions,
    IOptions<AssemblyAiOptions> Options,
    ITranscriptionProvider Provider,
    TimeProvider Clock,
    ILogger<TranscriptionJobService> Logger)
{
    private string Table(string Name) => $"{new NpgsqlCommandBuilder().QuoteIdentifier(MemoryOptions.Value.Schema)}.{new NpgsqlCommandBuilder().QuoteIdentifier(Name)}";

    public async Task<TranscriptionResponse> GetAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken)
    {
        await using var Connection = new NpgsqlConnection(MemoryOptions.Value.ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        // Resolve ownership and audio from server storage, never from a binary bus payload.
        string MimeType;
        await using (var Upload = new NpgsqlCommand($"SELECT mime_type FROM {Table("coach_call_uploads")} WHERE upload_id = @id AND profile_id = @profile", Connection))
        {
            Upload.Parameters.AddWithValue("id", UploadId);
            Upload.Parameters.AddWithValue("profile", ProfileId);
            await using var Reader = await Upload.ExecuteReaderAsync(CancellationToken);
            if (!await Reader.ReadAsync(CancellationToken)) return Failed(UploadId, "Upload not found.");
            MimeType = Reader.GetString(0);
        }

        // The committed insert grants exactly one caller permission to submit. If that
        // caller dies before saving its provider ID, never silently resubmit a paid job.
        var Created = false;
        await using (var Insert = new NpgsqlCommand($"INSERT INTO {Table("transcription_jobs")} (upload_id, deadline) VALUES (@id, @deadline) ON CONFLICT DO NOTHING", Connection))
        {
            Insert.Parameters.AddWithValue("id", UploadId);
            Insert.Parameters.AddWithValue("deadline", Clock.GetUtcNow().AddMinutes(Options.Value.TranscriptionTimeoutMinutes));
            Created = await Insert.ExecuteNonQueryAsync(CancellationToken) == 1;
        }
        if (Created)
        {
            await using var ReadAudio = new NpgsqlCommand($"SELECT audio_bytes FROM {Table("coach_call_uploads")} WHERE upload_id = @id AND profile_id = @profile", Connection);
            ReadAudio.Parameters.AddWithValue("id", UploadId);
            ReadAudio.Parameters.AddWithValue("profile", ProfileId);
            var Audio = await ReadAudio.ExecuteScalarAsync(CancellationToken) as byte[];
            if (Audio is null) return await SaveAsync(Connection, Failed(UploadId, "Upload audio is unavailable."), CancellationToken);
            try
            {
                var ProviderId = await Provider.SubmitAsync(Audio, MimeType, CancellationToken);
                await using var SaveId = new NpgsqlCommand($"UPDATE {Table("transcription_jobs")} SET provider_job_id = @provider WHERE upload_id = @id", Connection);
                SaveId.Parameters.AddWithValue("id", UploadId);
                SaveId.Parameters.AddWithValue("provider", ProviderId);
                await SaveId.ExecuteNonQueryAsync(CancellationToken);
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
            catch (Exception Error)
            {
                Logger.LogError(Error, "Transcription submission requires review for upload {UploadId}", UploadId);
                return await SaveAsync(Connection, Failed(UploadId, "Transcription submission could not be confirmed; operator review required."), CancellationToken);
            }
        }

        string? ProviderJobId;
        DateTimeOffset Deadline;
        await using (var Query = new NpgsqlCommand($"SELECT provider_job_id, deadline, result::text FROM {Table("transcription_jobs")} WHERE upload_id = @id", Connection))
        {
            Query.Parameters.AddWithValue("id", UploadId);
            await using var Reader = await Query.ExecuteReaderAsync(CancellationToken);
            if (!await Reader.ReadAsync(CancellationToken)) return Failed(UploadId, "Transcription job no longer exists.");
            if (!Reader.IsDBNull(2)) return JsonSerializer.Deserialize<TranscriptionResponse>(Reader.GetString(2))!;
            ProviderJobId = Reader.IsDBNull(0) ? null : Reader.GetString(0);
            Deadline = Reader.GetFieldValue<DateTimeOffset>(1);
        }
        if (Clock.GetUtcNow() >= Deadline)
            return await SaveAsync(Connection, Failed(UploadId, ProviderJobId is null
                ? "Transcription submission could not be confirmed; operator review required."
                : "Transcription timed out."), CancellationToken);
        if (ProviderJobId is null) return new(UploadId, TranscriptionStatus.Pending, []);

        try
        {
            var Result = await Provider.GetResultAsync(UploadId, ProviderJobId, CancellationToken);
            if (Result.Status == TranscriptionStatus.Completed && Result.Segments.Count == 0)
                Result = Failed(UploadId, "Transcription completed without utterances.");
            return Result.Status == TranscriptionStatus.Pending ? Result : await SaveAsync(Connection, Result, CancellationToken);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception Error)
        {
            Logger.LogWarning(Error, "Unable to poll transcription for upload {UploadId}; will retry", UploadId);
            return new(UploadId, TranscriptionStatus.Pending, [], RetryAfterSeconds: 10);
        }
    }

    private async Task<TranscriptionResponse> SaveAsync(NpgsqlConnection Connection, TranscriptionResponse Result, CancellationToken CancellationToken)
    {
        // First terminal result wins, even when requests overlap or responses are lost.
        await using var Save = new NpgsqlCommand($"UPDATE {Table("transcription_jobs")} SET result = COALESCE(result, @result::jsonb) WHERE upload_id = @id RETURNING result::text", Connection);
        Save.Parameters.AddWithValue("id", Result.JobId);
        Save.Parameters.AddWithValue("result", JsonSerializer.Serialize(Result));
        return JsonSerializer.Deserialize<TranscriptionResponse>((string)(await Save.ExecuteScalarAsync(CancellationToken))!)!;
    }

    private static TranscriptionResponse Failed(Guid UploadId, string Error) => new(UploadId, TranscriptionStatus.Failed, [], Error);
}
