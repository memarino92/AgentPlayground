using Npgsql;
using PersonalAgent.Contracts.Automations;
using System.Text.Json;

namespace PersonalAgent.Integrations;

public sealed class ScratchpadStore(IntegrationDatabase Database)
{
    public async Task<string?> ScopeAsync(Guid Run, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("SELECT scope FROM app.scratch_cells WHERE run_id = $1", Connection);
        Command.Parameters.AddWithValue(Run);
        return await Command.ExecuteScalarAsync(Token) as string;
    }

    // Hold a transaction-scoped lock across the remote cell. Its progress and claim use separate committed writes.
    public async Task<NpgsqlConnection> LockAsync(string Scope, CancellationToken Token)
    {
        var Connection = await Database.OpenAsync(Token);
        try
        {
            await Connection.BeginTransactionAsync(Token);
            await using var Command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 50))", Connection);
            Command.Parameters.AddWithValue(Scope);
            await Command.ExecuteNonQueryAsync(Token);
            return Connection;
        }
        catch { await Connection.DisposeAsync(); throw; }
    }

    public async Task<string?> BeginAsync(Guid Run, string Scope, long Revision, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            WITH previous AS (SELECT sandbox_id, revision, reusable, started_at FROM app.scratch_cells
                WHERE scope=$2 AND started_at IS NOT NULL ORDER BY started_at DESC LIMIT 1)
            UPDATE app.scratch_cells SET started_at=clock_timestamp(), revision=$3 WHERE run_id=$1 AND started_at IS NULL
            RETURNING (SELECT sandbox_id FROM previous WHERE reusable AND revision=$3 AND started_at > now()-interval '10 minutes')
            """, Connection);
        Command.Parameters.AddWithValue(Run);
        Command.Parameters.AddWithValue(Scope);
        Command.Parameters.AddWithValue(Revision);
        return await Command.ExecuteScalarAsync(Token) as string;
    }

    public async Task WriteProgressAsync(Guid Run, string Output, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("UPDATE app.scratch_cells SET progress=$2 WHERE run_id=$1", Connection);
        Command.Parameters.AddWithValue(Run);
        Command.Parameters.AddWithValue(Output.Replace("\0", ""));
        await Command.ExecuteNonQueryAsync(Token);
    }

    public async Task<(string Output, string? Status)> ProgressAsync(Guid Run, string Scope, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            SELECT c.progress,s."Status" FROM app.scratch_cells c JOIN automation."Steps" s ON s."RunId"=c.run_id
            WHERE c.run_id=$1 AND c.scope=$2 AND s."Index"=0
            """, Connection);
        Command.Parameters.AddWithValue(Run);
        Command.Parameters.AddWithValue(Scope);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        return await Reader.ReadAsync(Token) ? (Reader.GetString(0), Reader.GetString(1)) : throw new KeyNotFoundException();
    }

    public async Task<(List<string> Cells, string Image, SandboxInputFile[] Files)> SourcesAsync(Guid Run, string Scope, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            SELECT c.source, step."Status", s."ProgramEvidence"::jsonb ->> 'ImageId', c.input_files::text FROM app.scratch_cells target JOIN app.scratch_cells c
            ON c.scope=target.scope AND c.sandbox_id=target.sandbox_id AND c.started_at <= target.started_at
            JOIN automation."Steps" step ON step."RunId"=c.run_id AND step."Index"=0
            JOIN automation."Steps" s ON s."RunId"=target.run_id AND s."Index"=0 AND s."Status"='Completed'
            WHERE target.run_id=$1 AND target.scope=$2 AND target.reusable AND c.reusable ORDER BY c.started_at
            """, Connection);
        Command.Parameters.AddWithValue(Run);
        Command.Parameters.AddWithValue(Scope);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        var Sources = new List<string>();
        var Files = new List<SandboxInputFile>();
        var Image = "";
        var Length = 0;
        while (await Reader.ReadAsync(Token))
        {
            Image = Reader.GetString(2);
            var Source = Reader.GetString(0);
            Length += Source.Length;
            if (Length > 24000) throw new ArgumentException("This session exceeds the recipe budget. Consolidate the calculation into a saved Python step.");
            if (Reader.GetString(1) != "Completed") throw new ArgumentException("This session contains a failed cell. Save the corrected calculation as a standalone Python recipe.");
            Files.AddRange(JsonSerializer.Deserialize<SandboxInputFile[]>(Reader.GetString(3))!);
            Sources.Add(Source);
        }
        var Inputs = Files.Distinct().ToArray();
        SandboxInputFile.Validate(Inputs);
        return (Sources, Image, Inputs);
    }
}
