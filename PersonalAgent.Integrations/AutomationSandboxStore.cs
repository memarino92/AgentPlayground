using System.Text.Json;
using Npgsql;
using PersonalAgent.Contracts.Automations;

namespace PersonalAgent.Integrations;

public sealed class SandboxLease(Guid RunId, int Step, string? SandboxId, DateTimeOffset ExpiresAt, string State, string Credential, string EnvironmentId)
{
    public Guid RunId { get; } = RunId;
    public int Step { get; } = Step;
    public string? SandboxId { get; } = SandboxId;
    public DateTimeOffset ExpiresAt { get; } = ExpiresAt;
    public string State { get; } = State;
    public string Credential { get; } = Credential;
    public string EnvironmentId { get; } = EnvironmentId;
}

public sealed class AutomationSandboxStore(IntegrationDatabase Database, AutomationRuntimeStore Runtime)
{
    public async Task<bool> ClaimAsync(Guid RunId, int Step, AutomationRuntimeSnapshot Snapshot, string TokenHash, CancellationToken Token, bool CustomImage = false)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            INSERT INTO app.automation_sandboxes(run_id, step, expires_at, credential, settings, token_hash)
            VALUES (@run, @step, now() + @lifetime, @credential, @settings, @hash) ON CONFLICT DO NOTHING
            """, Connection);
        AddIdentity(Command, RunId, Step);
        Command.Parameters.AddWithValue("credential", Runtime.Protect(Snapshot.Token));
        Command.Parameters.AddWithValue("settings", JsonSerializer.Serialize(Snapshot.Settings));
        Command.Parameters.AddWithValue("hash", TokenHash);
        Command.Parameters.AddWithValue("lifetime", TimeSpan.FromMinutes(CustomImage ? 16 : 4));
        return await Command.ExecuteNonQueryAsync(Token) == 1;
    }

    public async Task AttachAsync(Guid RunId, int Step, string SandboxId, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            UPDATE app.automation_sandboxes SET sandbox_id = @id, state = 'Running'
            WHERE run_id = @run AND step = @step AND state = 'Creating' AND expires_at > now()
            """, Connection);
        AddIdentity(Command, RunId, Step); Command.Parameters.AddWithValue("id", SandboxId);
        if (await Command.ExecuteNonQueryAsync(Token) != 1) throw new InvalidOperationException("Sandbox lease expired before execution.");
    }

    public async Task SaveResultAsync(Guid RunId, int Step, string Result, CancellationToken Token, byte[]? Archive = null, IReadOnlyList<SandboxArtifact>? Artifacts = null, bool Released = false)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            WITH saved AS (UPDATE app.automation_sandboxes SET result = @result, artifact_zip = @archive, artifact_manifest = @manifest, credential = CASE WHEN @released THEN '' ELSE credential END, state = CASE WHEN @released THEN 'Released' WHEN state = 'Destroyed' THEN state ELSE 'CleanupPending' END, token_hash = ''
            WHERE run_id = @run AND step = @step RETURNING run_id, sandbox_id)
            UPDATE app.scratch_cells c SET reusable=true, sandbox_id=s.sandbox_id
            FROM saved s WHERE @released AND c.run_id=s.run_id
            """, Connection);
        AddIdentity(Command, RunId, Step);
        Command.Parameters.AddWithValue("result", Result);
        Command.Parameters.AddWithValue("released", Released);
        Command.Parameters.Add("archive", NpgsqlTypes.NpgsqlDbType.Bytea).Value = (object?)Archive ?? DBNull.Value;
        Command.Parameters.Add("manifest", NpgsqlTypes.NpgsqlDbType.Text).Value = Artifacts is null ? DBNull.Value : JsonSerializer.Serialize(Artifacts);
        await Command.ExecuteNonQueryAsync(Token);
    }

    public async Task<byte[]?> ReadArchiveAsync(Guid RunId, int Step, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("SELECT artifact_zip FROM app.automation_sandboxes WHERE run_id = @run AND step = @step", Connection);
        AddIdentity(Command, RunId, Step);
        return await Command.ExecuteScalarAsync(Token) as byte[];
    }

    public async Task<string?> ResultAsync(Guid RunId, int Step, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("SELECT result FROM app.automation_sandboxes WHERE run_id = @run AND step = @step", Connection);
        AddIdentity(Command, RunId, Step);
        return await Command.ExecuteScalarAsync(Token) as string;
    }

    public async Task<bool> AuthorizeAsync(Guid RunId, int Step, string Hash, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM app.automation_sandboxes WHERE run_id = @run AND step = @step
                AND token_hash = @hash AND state = 'Running' AND expires_at > now())
            """, Connection);
        AddIdentity(Command, RunId, Step); Command.Parameters.AddWithValue("hash", Hash);
        return (bool)(await Command.ExecuteScalarAsync(Token))!;
    }

    public async Task<IReadOnlyList<SandboxLease>> CleanupAsync(CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            SELECT run_id, step, sandbox_id, expires_at, state, credential, settings FROM app.automation_sandboxes
            WHERE state NOT IN ('Destroyed','Released') AND (expires_at <= now() OR state = 'CleanupPending') ORDER BY expires_at LIMIT 50
            """, Connection);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        var Items = new List<SandboxLease>();
        while (await Reader.ReadAsync(Token)) Items.Add(new(Reader.GetGuid(0), Reader.GetInt32(1), Reader.IsDBNull(2) ? null : Reader.GetString(2),
            Reader.GetFieldValue<DateTimeOffset>(3), Reader.GetString(4), Runtime.Unprotect(Reader.GetString(5)), JsonSerializer.Deserialize<AutomationRuntimeSettings>(Reader.GetString(6))!.EnvironmentId));
        return Items;
    }

    public async Task DestroyedAsync(Guid RunId, int Step, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            UPDATE app.automation_sandboxes SET state = 'Destroyed', token_hash = '', credential = ''
            WHERE run_id = @run AND step = @step
            """, Connection);
        AddIdentity(Command, RunId, Step); await Command.ExecuteNonQueryAsync(Token);
    }

    public static void AddIdentity(NpgsqlCommand Command, Guid RunId, int Step)
    { Command.Parameters.AddWithValue("run", RunId); Command.Parameters.AddWithValue("step", Step); }

    public async Task<IReadOnlyList<AutomationSandboxStatus>> StatusAsync(Guid RunId, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("SELECT step, sandbox_id, state, expires_at, artifact_manifest FROM app.automation_sandboxes WHERE run_id = @run ORDER BY step", Connection);
        Command.Parameters.AddWithValue("run", RunId);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        var Items = new List<AutomationSandboxStatus>();
        while (await Reader.ReadAsync(Token)) Items.Add(new(Reader.GetInt32(0), Reader.IsDBNull(1) ? null : Reader.GetString(1), Reader.GetString(2), Reader.GetFieldValue<DateTimeOffset>(3),
            Reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<SandboxArtifact[]>(Reader.GetString(4))));
        return Items;
    }
}
