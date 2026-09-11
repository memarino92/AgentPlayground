using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class CoachEvidenceService(
    IOptions<AgentMemoryOptions> Options,
    IOptions<CoachCheckinOptions> UploadOptions,
    CoachCheckinService Checkins) : ICoachEvidenceService
{
    private readonly string Table = $"{new NpgsqlCommandBuilder().QuoteIdentifier(Options.Value.Schema)}.coach_call_uploads";

    public async Task<CoachEvidenceResponse?> GetAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken)
    {
        var Transcript = await Checkins.GetTranscriptAsync(UploadId, ProfileId, CancellationToken);
        if (Transcript is null) return null;
        await using var Connection = new NpgsqlConnection(Options.Value.ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        await using var Command = new NpgsqlCommand($"SELECT audio_bytes IS NOT NULL FROM {Table} WHERE upload_id = @id AND profile_id = @profile", Connection);
        Bind(Command, UploadId, ProfileId);
        return new(Transcript, await Command.ExecuteScalarAsync(CancellationToken) is true, UploadOptions.Value.MaxUploadMb * 1024L * 1024L);
    }

    public async Task<CoachAudio?> GetAudioAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken)
    {
        await using var Connection = new NpgsqlConnection(Options.Value.ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        await using var Command = new NpgsqlCommand($"SELECT audio_bytes, mime_type FROM {Table} WHERE upload_id = @id AND profile_id = @profile AND audio_bytes IS NOT NULL", Connection);
        Bind(Command, UploadId, ProfileId);
        await using var Reader = await Command.ExecuteReaderAsync(CancellationToken);
        if (!await Reader.ReadAsync(CancellationToken)) return null;
        // Upload MIME types are client supplied; never serve active document content inline.
        var ContentType = Reader.GetString(1).ToLowerInvariant() switch
        {
            "audio/mpeg" => "audio/mpeg",
            "audio/mp4" or "audio/m4a" or "audio/x-m4a" => "audio/mp4",
            "audio/wav" or "audio/x-wav" => "audio/wav",
            "audio/ogg" => "audio/ogg",
            "audio/webm" => "audio/webm",
            "audio/flac" => "audio/flac",
            _ => "application/octet-stream"
        };
        return new(Reader.GetFieldValue<byte[]>(0), ContentType);
    }

    public async Task<DeleteCoachAudioResult> DeleteAudioAsync(Guid UploadId, string ProfileId, CancellationToken CancellationToken)
    {
        await using var Connection = new NpgsqlConnection(Options.Value.ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        await using var Transaction = await Connection.BeginTransactionAsync(CancellationToken);
        await using var Command = new NpgsqlCommand($"SELECT status FROM {Table} WHERE upload_id = @id AND profile_id = @profile FOR UPDATE", Connection, Transaction);
        Bind(Command, UploadId, ProfileId);
        var Status = await Command.ExecuteScalarAsync(CancellationToken);
        if (Status is null) return DeleteCoachAudioResult.NotFound;
        if (Status is not ("Completed" or "Failed")) return DeleteCoachAudioResult.Pending;
        Command.CommandText = $"UPDATE {Table} SET audio_bytes = NULL, updated_at = now() WHERE upload_id = @id AND profile_id = @profile AND audio_bytes IS NOT NULL";
        await Command.ExecuteNonQueryAsync(CancellationToken);
        await Transaction.CommitAsync(CancellationToken);
        return DeleteCoachAudioResult.Deleted;
    }

    private static void Bind(NpgsqlCommand Command, Guid UploadId, string ProfileId)
    {
        Command.Parameters.AddWithValue("id", UploadId);
        Command.Parameters.AddWithValue("profile", ProfileId);
    }

    public async Task<AttachCoachAudioResult> AttachAudioAsync(Guid UploadId, string ProfileId, byte[] Bytes, string ContentType, CancellationToken CancellationToken)
    {
        await using var Connection = new NpgsqlConnection(Options.Value.ConnectionString);
        await Connection.OpenAsync(CancellationToken);
        await using var Transaction = await Connection.BeginTransactionAsync(CancellationToken);
        await using var Command = new NpgsqlCommand($"SELECT status FROM {Table} WHERE upload_id = @id AND profile_id = @profile FOR UPDATE", Connection, Transaction);
        Bind(Command, UploadId, ProfileId);
        var Status = await Command.ExecuteScalarAsync(CancellationToken);
        if (Status is null) return AttachCoachAudioResult.NotFound;
        if (Status is not "Completed") return AttachCoachAudioResult.NotCompleted;
        // Keep ingestion identity/hash and all processed evidence unchanged. The Owner
        // chooses the recording; attaching audio never queues transcription or embeddings.
        Command.CommandText = $"UPDATE {Table} SET audio_bytes = @bytes, mime_type = @mime, size_bytes = @size, updated_at = now() WHERE upload_id = @id AND profile_id = @profile";
        Command.Parameters.AddWithValue("bytes", Bytes);
        Command.Parameters.AddWithValue("mime", ContentType);
        Command.Parameters.AddWithValue("size", Bytes.LongLength);
        await Command.ExecuteNonQueryAsync(CancellationToken);
        await Transaction.CommitAsync(CancellationToken);
        return AttachCoachAudioResult.Stored;
    }
}
