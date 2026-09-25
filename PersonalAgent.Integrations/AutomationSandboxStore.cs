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
    public async Task<bool> ClaimAsync(Guid RunId, int Step, AutomationRuntimeSnapshot Snapshot, string TokenHash, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            INSERT INTO app.automation_sandboxes(run_id, step, expires_at, credential, settings, token_hash)
            VALUES (@run, @step, now() + interval '4 minutes', @credential, @settings, @hash) ON CONFLICT DO NOTHING
            """, Connection);
        AddIdentity(Command, RunId, Step);
        Command.Parameters.AddWithValue("credential", Runtime.Protect(Snapshot.Token));
        Command.Parameters.AddWithValue("settings", JsonSerializer.Serialize(Snapshot.Settings));
        Command.Parameters.AddWithValue("hash", TokenHash);
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

    public async Task SaveResultAsync(Guid RunId, int Step, string Result, CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            UPDATE app.automation_sandboxes SET result = @result, state = CASE WHEN state = 'Destroyed' THEN state ELSE 'CleanupPending' END, token_hash = ''
            WHERE run_id = @run AND step = @step
            """, Connection);
        AddIdentity(Command, RunId, Step); Command.Parameters.AddWithValue("result", Result);
        await Command.ExecuteNonQueryAsync(Token);
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
            WHERE state != 'Destroyed' AND (expires_at <= now() OR state = 'CleanupPending') ORDER BY expires_at LIMIT 50
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
        await using var Command = new NpgsqlCommand("SELECT step, sandbox_id, state, expires_at FROM app.automation_sandboxes WHERE run_id = @run ORDER BY step", Connection);
        Command.Parameters.AddWithValue("run", RunId);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        var Items = new List<AutomationSandboxStatus>();
        while (await Reader.ReadAsync(Token)) Items.Add(new(Reader.GetInt32(0), Reader.IsDBNull(1) ? null : Reader.GetString(1), Reader.GetString(2), Reader.GetFieldValue<DateTimeOffset>(3)));
        return Items;
    }
}
