using System.Text.Json;
using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Commands;
using AgentPlayground.Contracts.Messaging.Events;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class ScheduledJobStore(IOptions<AgentMemoryOptions> Options)
{
    private string Schema => Options.Value.Schema;
    private string Table => $"{new NpgsqlCommandBuilder().QuoteIdentifier(Schema)}.scheduled_jobs";
    private string Attempts => $"{new NpgsqlCommandBuilder().QuoteIdentifier(Schema)}.scheduled_job_attempts";

    public static string SchemaSql(string Schema)
    {
        var Prefix = new NpgsqlCommandBuilder().QuoteIdentifier(Schema);
        return $"""
            CREATE TABLE IF NOT EXISTS {Prefix}.scheduled_jobs (
                task_id uuid PRIMARY KEY, subject_id text NOT NULL, actor_id text NULL,
                execute_at timestamptz NOT NULL, created_at timestamptz NOT NULL,
                next_dispatch_at timestamptz NOT NULL, status text NOT NULL, data jsonb NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_scheduled_jobs_subject ON {Prefix}.scheduled_jobs(subject_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_scheduled_jobs_due ON {Prefix}.scheduled_jobs(next_dispatch_at)
                WHERE status IN ('Scheduled', 'Retrying', 'Running');
            CREATE TABLE IF NOT EXISTS {Prefix}.scheduled_job_attempts (
                task_id uuid NOT NULL REFERENCES {Prefix}.scheduled_jobs(task_id), number integer NOT NULL,
                started_at timestamptz NOT NULL, finished_at timestamptz NULL, status text NOT NULL, outcome text NULL,
                PRIMARY KEY(task_id, number));
            """;
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken Token)
    {
        var Connection = new NpgsqlConnection(Options.Value.ConnectionString);
        try { await Connection.OpenAsync(Token); return Connection; }
        catch { await Connection.DisposeAsync(); throw; }
    }

    public async Task<bool> TryLockAsync(NpgsqlConnection Connection, Guid Id, CancellationToken Token)
    {
        await using var Command = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id, 210021))", Connection);
        Command.Parameters.AddWithValue("id", Id.ToString());
        return (bool)(await Command.ExecuteScalarAsync(Token))!;
    }

    public async Task UnlockAsync(NpgsqlConnection Connection, Guid Id)
    {
        if (Connection.State != System.Data.ConnectionState.Open) return;
        await using var Command = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id, 210021))", Connection);
        Command.Parameters.AddWithValue("id", Id.ToString());
        await Command.ExecuteNonQueryAsync();
    }

    public async Task<ScheduledJob?> GetAsync(Guid Id, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        return await GetAsync(Connection, Id, Token);
    }

    public async Task<ScheduledJob?> GetAsync(NpgsqlConnection Connection, Guid Id, CancellationToken Token)
    {
        await using var Command = new NpgsqlCommand($"SELECT data::text FROM {Table} WHERE task_id = @id", Connection);
        Command.Parameters.AddWithValue("id", Id);
        return await Command.ExecuteScalarAsync(Token) is string Json ? JsonSerializer.Deserialize<ScheduledJob>(Json) : null;
    }

    public async Task CreateAsync(ScheduledJob Job, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Transaction = await Connection.BeginTransactionAsync(Token);
        await using var Command = new NpgsqlCommand($"""
            INSERT INTO {Table}(task_id, subject_id, actor_id, execute_at, created_at, next_dispatch_at, status, data)
            VALUES (@id, @subject, @actor, @due, @created, @due, @status, @data::jsonb)
            ON CONFLICT DO NOTHING
            """, Connection, Transaction);
        Command.Parameters.AddWithValue("id", Job.TaskId);
        Command.Parameters.AddWithValue("subject", Job.SubjectProfileId);
        Command.Parameters.AddWithValue("actor", NpgsqlTypes.NpgsqlDbType.Text, (object?)Job.ActorId ?? DBNull.Value);
        Command.Parameters.AddWithValue("due", Job.ExecuteAt);
        Command.Parameters.AddWithValue("created", Job.CreatedAt);
        Command.Parameters.AddWithValue("status", Job.Status);
        Command.Parameters.AddWithValue("data", JsonSerializer.Serialize(Job));
        var Inserted = await Command.ExecuteNonQueryAsync(Token);
        if (Inserted == 1 && Job.Status == "Scheduled")
        {
            var (Tenant, User) = SplitSubject(Job.SubjectProfileId);
            await CoachCallOutbox.EnqueueAsync(Transaction, Schema, new AgentTaskScheduled
            {
                TaskId = Job.TaskId, TenantId = Tenant, UserId = User, CorrelationId = Job.CorrelationId,
                RequestedAtUtc = Job.CreatedAt, ExecuteAtUtc = Job.ExecuteAt, Instruction = "", NotifyOnCompletion = false
            }, Token);
        }
        await Transaction.CommitAsync(Token);
    }

    // The caller holds the session advisory lock. State, attempt outcome and notification commit together.
    public async Task SaveAsync(NpgsqlConnection Connection, ScheduledJob Job, bool StartAttempt, bool FinishAttempt, bool Notify, CancellationToken Token)
    {
        await using var Transaction = await Connection.BeginTransactionAsync(Token);
        await using var Command = new NpgsqlCommand($"""
            UPDATE {Table} SET status = @status, data = @data::jsonb, next_dispatch_at = now() + interval '1 minute' WHERE task_id = @id
                AND status NOT IN ('Completed', 'Blocked', 'Cancelled', 'Failed', 'NeedsReview')
            """, Connection, Transaction);
        Command.Parameters.AddWithValue("id", Job.TaskId);
        Command.Parameters.AddWithValue("status", Job.Status);
        Command.Parameters.AddWithValue("data", JsonSerializer.Serialize(Job));
        if (await Command.ExecuteNonQueryAsync(Token) == 0) return;
        if (StartAttempt || FinishAttempt)
        {
            Command.Parameters.Clear();
            Command.CommandText = StartAttempt
                ? $"INSERT INTO {Attempts}(task_id, number, started_at, status) VALUES (@id, @number, now(), @status)"
                : $"UPDATE {Attempts} SET finished_at = now(), status = @status, outcome = @outcome WHERE task_id = @id AND number = @number";
            Command.Parameters.AddWithValue("id", Job.TaskId);
            Command.Parameters.AddWithValue("number", Job.AttemptCount);
            Command.Parameters.AddWithValue("status", Job.Status);
            if (FinishAttempt) Command.Parameters.AddWithValue("outcome", NpgsqlTypes.NpgsqlDbType.Text, (object?)Job.Outcome ?? DBNull.Value);
            await Command.ExecuteNonQueryAsync(Token);
        }
        else if (Job.Status == "Running")
        {
            Command.Parameters.Clear();
            Command.CommandText = $"UPDATE {Attempts} SET status = 'Running' WHERE task_id = @id AND number = @number";
            Command.Parameters.AddWithValue("id", Job.TaskId);
            Command.Parameters.AddWithValue("number", Job.AttemptCount);
            await Command.ExecuteNonQueryAsync(Token);
        }
        if (Notify && Job.NotifyOnCompletion && Job.ActorId is not null)
        {
            var (Tenant, User) = SplitSubject(Job.SubjectProfileId);
            await CoachCallOutbox.EnqueueAsync(Transaction, Schema, new NotificationRequested
            {
                NotificationId = Job.TaskId, TenantId = Tenant, UserId = User, CorrelationId = Job.CorrelationId,
                RequestedAtUtc = Job.UpdatedAt, ExecuteAtUtc = Job.UpdatedAt,
                Title = Job.Status == "Completed" ? "Scheduled task complete" : "Scheduled task needs attention",
                Body = "Open Scheduled Jobs to view the outcome.", DeepLink = $"/jobs?jobId={Job.TaskId}", Source = "ScheduledJob"
            }, Token);
        }
        await Transaction.CommitAsync(Token);
    }

    public async Task<IReadOnlyList<ScheduledJob>> ListAsync(string Subject, string? Actor, string? Status, DateTimeOffset? Before, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Command = new NpgsqlCommand($"""
            SELECT data::text FROM {Table} WHERE lower(subject_id) = lower(@subject)
                AND (@actor IS NULL OR actor_id = @actor) AND (@status IS NULL OR status = @status)
                AND (@before IS NULL OR created_at < @before) ORDER BY created_at DESC, task_id LIMIT 50
            """, Connection);
        Command.Parameters.AddWithValue("subject", Subject);
        Command.Parameters.AddWithValue("actor", NpgsqlTypes.NpgsqlDbType.Text, (object?)Actor ?? DBNull.Value);
        Command.Parameters.AddWithValue("status", NpgsqlTypes.NpgsqlDbType.Text, (object?)Status ?? DBNull.Value);
        Command.Parameters.AddWithValue("before", NpgsqlTypes.NpgsqlDbType.TimestampTz, (object?)Before ?? DBNull.Value);
        var Jobs = new List<ScheduledJob>();
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        while (await Reader.ReadAsync(Token)) Jobs.Add(JsonSerializer.Deserialize<ScheduledJob>(Reader.GetString(0))!);
        return Jobs;
    }

    public async Task<IReadOnlyList<ScheduledJobAttempt>> GetAttemptsAsync(Guid Id, CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Command = new NpgsqlCommand($"SELECT number, started_at, finished_at, status, outcome FROM {Attempts} WHERE task_id = @id ORDER BY number", Connection);
        Command.Parameters.AddWithValue("id", Id);
        var Items = new List<ScheduledJobAttempt>();
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        while (await Reader.ReadAsync(Token)) Items.Add(new(Reader.GetInt32(0), Reader.GetFieldValue<DateTimeOffset>(1),
            Reader.IsDBNull(2) ? null : Reader.GetFieldValue<DateTimeOffset>(2), Reader.GetString(3), Reader.IsDBNull(4) ? null : Reader.GetString(4)));
        return Items;
    }

    public async Task ReconcileAsync(CancellationToken Token)
    {
        await using var Connection = await OpenAsync(Token);
        await using var Transaction = await Connection.BeginTransactionAsync(Token);
        await using var Command = new NpgsqlCommand($"""
            UPDATE {Table} SET next_dispatch_at = now() + interval '1 minute'
            WHERE task_id IN (SELECT task_id FROM {Table} WHERE status IN ('Scheduled', 'Retrying', 'Running')
                AND next_dispatch_at <= now() AND execute_at <= now() ORDER BY next_dispatch_at LIMIT 20 FOR UPDATE SKIP LOCKED)
            RETURNING data::text
            """, Connection, Transaction);
        var Jobs = new List<ScheduledJob>();
        await using (var Reader = await Command.ExecuteReaderAsync(Token))
            while (await Reader.ReadAsync(Token)) Jobs.Add(JsonSerializer.Deserialize<ScheduledJob>(Reader.GetString(0))!);
        foreach (var Job in Jobs)
        {
            var (Tenant, User) = SplitSubject(Job.SubjectProfileId);
            await CoachCallOutbox.EnqueueAsync(Transaction, Schema, new ExecuteAgentTask
            {
                TaskId = Job.TaskId, TenantId = Tenant, UserId = User, CorrelationId = Job.CorrelationId,
                ExecuteAtUtc = Job.ExecuteAt, Instruction = "", NotifyOnCompletion = false
            }, Token);
        }
        await Transaction.CommitAsync(Token);
    }

    public static (string Tenant, string User) SplitSubject(string Subject)
    {
        var Parts = Subject.Split(':', 2);
        return Parts.Length == 2 ? (Parts[0], Parts[1]) : ("default", Subject);
    }

    public static string Subject(string Tenant, string User) => string.IsNullOrWhiteSpace(Tenant) || Tenant == "default" ? User.Trim() : $"{Tenant.Trim()}:{User.Trim()}";
}
