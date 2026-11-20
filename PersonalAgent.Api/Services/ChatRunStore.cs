using Microsoft.Extensions.Options;
using Npgsql;

using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Contracts;

namespace PersonalAgent.Api.Services;

internal sealed class ChatRunStore(IOptions<AgentMemoryOptions> Options) : IAsyncDisposable
{
    private readonly NpgsqlDataSource database = NpgsqlDataSource.Create(Options.Value.ConnectionString);
    private readonly string schema = new NpgsqlCommandBuilder().QuoteIdentifier(Options.Value.Schema);
    private readonly string sessions = new NpgsqlCommandBuilder().QuoteIdentifier(Options.Value.SessionsTableName);
    internal record Run(Guid Id, Guid Session, AgentAccessContext Access, string Message, string State, bool Stop, long Baseline);
    internal record Page(List<(long Sequence, string Frame)> Events, string State);

    private NpgsqlCommand Command(string sql, params object[] values)
    {
        var command = database.CreateCommand(sql);
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
        return command;
    }

    public async Task<bool> SubmitAsync(Guid session, Guid id, AgentAccessContext access, string message, CancellationToken token)
    {
        await using var insert = Command($"""
            INSERT INTO {schema}.chat_runs(id, session_id, actor, role, subject, email, message, baseline)
            SELECT $1,$2,$3,$4,$5,$6,$7,last_message_seq FROM {schema}.{sessions} WHERE session_id=$2
            ON CONFLICT DO NOTHING
            """, id, session, access.ActorId, access.Role, access.SubjectProfileId, access.Email ?? "", message);
        if (await insert.ExecuteNonQueryAsync(token) == 1) return true;
        var existing = await FindAsync(id, token);
        return existing is not null && existing.Session == session && existing.Message == message && Matches(existing, access);
    }

    public static bool Matches(Run run, AgentAccessContext access) =>
        run.Access.ActorId.Equals(access.ActorId, StringComparison.OrdinalIgnoreCase) && run.Access.Role == access.Role &&
        run.Access.SubjectProfileId.Equals(access.SubjectProfileId, StringComparison.OrdinalIgnoreCase);

    public async Task<Run?> FindAsync(Guid id, CancellationToken token)
    {
        await using var command = Command($"SELECT session_id,actor,role,subject,email,message,state,stop_requested,baseline FROM {schema}.chat_runs WHERE id=$1", id);
        await using var r = await command.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? new(id, r.GetGuid(0), new(r.GetString(1), r.GetString(2), r.GetString(3)) { Email = r.GetString(4) },
            r.GetString(5), r.GetString(6), r.GetBoolean(7), r.GetInt64(8)) : null;
    }

    public async Task<PendingChatRun?> LatestAsync(Guid session, CancellationToken token)
    {
        await using var command = Command($"SELECT id,message,baseline,state FROM {schema}.chat_runs WHERE session_id=$1 ORDER BY created_at DESC LIMIT 1", session);
        await using var r = await command.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token) || r.GetString(3) is not ("running" or "queued")) return null;
        return new(r.GetGuid(0), r.GetString(1), r.GetInt64(2));
    }

    public async Task<List<(Guid Id, bool Stop)>> CandidatesAsync(CancellationToken token)
    {
        await using var command = Command($"SELECT id,stop_requested FROM {schema}.chat_runs WHERE state IN ('queued','running') ORDER BY (state='queued') DESC,created_at");
        await using var r = await command.ExecuteReaderAsync(token);
        var ids = new List<(Guid, bool)>();
        while (await r.ReadAsync(token)) ids.Add((r.GetGuid(0), r.GetBoolean(1)));
        return ids;
    }

    // The transaction owns only the advisory lock. Progress commits independently for live readers.
    public async Task<NpgsqlConnection?> TryLockAsync(Guid id, CancellationToken token)
    {
        var connection = await database.OpenConnectionAsync(token);
        try
        {
            await connection.BeginTransactionAsync(token);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended('chat-run:' || $1::text, 0))", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = id });
            if (await command.ExecuteScalarAsync(token) is true) return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
        await connection.DisposeAsync();
        return null;
    }

    public async Task<bool> TryStartAsync(Guid id, CancellationToken token)
    {
        await using var command = Command($"UPDATE {schema}.chat_runs SET state='running' WHERE id=$1 AND state='queued' AND NOT stop_requested", id);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }

    public async Task FinishAsync(Guid id, string state, CancellationToken token)
    {
        // Keep call outcomes and the idempotency key. The transcript owns final text;
        // transient deltas (including sandbox output) only need to survive an active run.
        await using var command = Command($"""
            WITH changed AS (UPDATE {schema}.chat_runs SET state=$2 WHERE id=$1 AND state IN ('queued','running') RETURNING id,state)
            DELETE FROM {schema}.chat_run_events e USING changed r WHERE e.run_id=r.id
              AND r.state IN ('completed','interrupted','stopped') AND e.frame::jsonb->>'type'='delta'
            """, id, state);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task StopAsync(Guid id, CancellationToken token)
    {
        await using var command = Command($"UPDATE {schema}.chat_runs SET stop_requested=true WHERE id=$1 AND state IN ('queued','running')", id);
        await command.ExecuteNonQueryAsync(token);
    }

    public async ValueTask AppendAsync(Guid id, string frame, CancellationToken token)
    {
        await using var command = Command($"""
            INSERT INTO {schema}.chat_run_events(run_id,frame)
            SELECT id,$2 FROM {schema}.chat_runs WHERE id=$1 AND state='running'
            """, id, frame);
        if (await command.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Chat run has ended.");
    }

    public async Task<Page> ReadAsync(Guid id, long after, CancellationToken token)
    {
        // Read state and frames in one snapshot. A terminal state follows the last committed frame.
        await using var command = Command($"""
            SELECT r.state,e.seq,e.frame FROM {schema}.chat_runs r LEFT JOIN LATERAL
            (SELECT seq,frame FROM {schema}.chat_run_events WHERE run_id=r.id AND seq>$2 ORDER BY seq LIMIT 128) e ON true
            WHERE r.id=$1 ORDER BY e.seq
            """, id, after);
        await using var r = await command.ExecuteReaderAsync(token);
        var rows = new List<(long, string)>();
        var state = "interrupted";
        while (await r.ReadAsync(token))
        {
            state = r.GetString(0);
            if (!r.IsDBNull(1)) rows.Add((r.GetInt64(1), r.GetString(2)));
        }
        return new(rows, state);
    }

    public ValueTask DisposeAsync() => database.DisposeAsync();
}
