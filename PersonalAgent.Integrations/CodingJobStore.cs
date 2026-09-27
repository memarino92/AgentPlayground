using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Npgsql;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Contracts.Configuration;

namespace PersonalAgent.Integrations;

public sealed class CodingSecrets(string OpenRouterKey, string GitHubPrivateKey)
{
    public string OpenRouterKey { get; } = OpenRouterKey;
    public string GitHubPrivateKey { get; } = GitHubPrivateKey;
}

public sealed record CodingJob
{
    public Guid Id { get; init; }
    public string ActorId { get; init; } = "";
    public string? Email { get; init; }
    public string Subject { get; init; } = "";
    public string Instruction { get; init; } = "";
    public CodingSettings Settings { get; init; } = new();
    public string BaseSha { get; init; } = "";
    public string Branch { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset Deadline { get; init; }
    public string Status { get; init; } = "Queued";
    public string? SandboxId { get; init; }
    public string CleanupState { get; init; } = "None";
    public string? PullRequestUrl { get; init; }
    public string? Error { get; init; }
    public CodingArtifact? Artifact { get; init; }
    public int ModelRequests { get; init; }
    [JsonIgnore] public CodingJobView View => new(Id, Instruction, Settings.Repository, BaseSha, Branch, Status, CreatedAt,
        Deadline, SandboxId, CleanupState, ModelRequests, PullRequestUrl, Error, Artifact);
}

// A committed job is also durable dispatch intent. Repeated sends are safe: only Queued can be claimed.
public sealed class CodingJobStore(IntegrationDatabase Database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task InitializeAsync(CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("""
            BEGIN;
            SELECT pg_advisory_xact_lock(947120038);
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.coding_settings(id int PRIMARY KEY CHECK(id=1), revision bigint NOT NULL,
                settings text NOT NULL, openrouter_key text NOT NULL, github_key text NOT NULL);
            CREATE TABLE IF NOT EXISTS app.coding_jobs(id uuid PRIMARY KEY, actor text NOT NULL, request_key text NOT NULL,
                state text NOT NULL, deadline timestamptz NOT NULL, payload text NOT NULL,
                capability_hash text NOT NULL DEFAULT '', requests int NOT NULL DEFAULT 0,
                lease_credential text NOT NULL DEFAULT '', lease_environment text NOT NULL DEFAULT '',
                UNIQUE(actor, request_key));
            COMMIT;
            """, C);
        await Q.ExecuteNonQueryAsync(Token);
    }

    public async Task<(CodingSettingsView View, CodingSecrets Secrets)> SettingsAsync(CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("SELECT revision, settings, openrouter_key, github_key FROM app.coding_settings WHERE id=1", C);
        await using var R = await Q.ExecuteReaderAsync(Token);
        if (!await R.ReadAsync(Token)) return (new(0, new(), false, false), new("", ""));
        var Key = Unprotect(R.GetString(2)); var GitHub = Unprotect(R.GetString(3));
        return (new(R.GetInt64(0), JsonSerializer.Deserialize<CodingSettings>(R.GetString(1), Json)!, Key.Length > 0, GitHub.Length > 0), new(Key, GitHub));
    }

    public async Task<CodingSettingsView> SaveSettingsAsync(SaveCodingSettings Request, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var T = await C.BeginTransactionAsync(Token);
        await using var Lock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(947120039)", C, T);
        await Lock.ExecuteNonQueryAsync(Token);
        var Current = await SettingsAsync(Token);
        if (Current.View.Revision != Request.ExpectedRevision) throw new IntegrationConflictException();
        var Key = EditSecret(Request.OpenRouterKeyAction, Request.OpenRouterKey, Current.Secrets.OpenRouterKey);
        var GitHub = EditSecret(Request.GitHubPrivateKeyAction, Request.GitHubPrivateKey, Current.Secrets.GitHubPrivateKey);
        ValidateSettings(Request.Settings);
        if (GitHub.Length > 0)
        {
            using var Rsa = RSA.Create();
            try { Rsa.ImportFromPem(GitHub); _ = Rsa.ExportParameters(true); }
            catch (Exception E) when (E is ArgumentException or CryptographicException) { throw new ArgumentException("A GitHub App private PEM key is required."); }
        }
        if (Request.Settings.Enabled && (Key.Length == 0 || GitHub.Length == 0)) throw new ArgumentException("Both provider credentials are required.");
        await using var Q = new NpgsqlCommand("""
            INSERT INTO app.coding_settings VALUES(1,@revision,@settings,@key,@github)
            ON CONFLICT(id) DO UPDATE SET revision=EXCLUDED.revision,settings=EXCLUDED.settings,openrouter_key=EXCLUDED.openrouter_key,github_key=EXCLUDED.github_key
            """, C, T);
        Q.Parameters.AddWithValue("revision", Current.View.Revision + 1);
        Q.Parameters.AddWithValue("settings", JsonSerializer.Serialize(Request.Settings, Json));
        Q.Parameters.AddWithValue("key", Protect(Key)); Q.Parameters.AddWithValue("github", Protect(GitHub));
        await Q.ExecuteNonQueryAsync(Token); await T.CommitAsync(Token);
        return new(Current.View.Revision + 1, Request.Settings, Key.Length > 0, GitHub.Length > 0);
    }

    public static void ValidateSettings(CodingSettings S)
    {
        if (S is null || !Regex.IsMatch(S.Repository ?? "", @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")
            || S.BaseBranch != "main" || S.MaxMinutes is < 5 or > 30 || S.MaxModelRequests is < 1 or > 12
            || S.ModelBudgetUsd is <= 0 or > 5 || S.MaxModelRequests * .40m > S.ModelBudgetUsd
            || !Regex.IsMatch(S.TestProject ?? "", @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.csproj\z")
            || (S.TestProject ?? "").Contains("..", StringComparison.Ordinal) || S.TestFilter is null || S.TestFilter.Length > 500)
            throw new ArgumentException("Use owner/repository, main, 5–30 minutes, 1–12 model requests covered by a budget of $0.40/request (at most $5), and a repository test project.");
        if (S.Enabled && (S.GitHubAppId <= 0 || S.InstallationId <= 0
            || !Regex.IsMatch(S.Checkpoint ?? "", @"\A[A-Za-z0-9_.-]{1,128}\z")
            || !Regex.IsMatch(S.ImageId ?? "", @"\Asha256:[a-f0-9]{64}\z")))
            throw new ArgumentException("Enabled coding requires a prepared checkpoint/image and GitHub App installation.");
    }

    public async Task<CodingJob> CreateAsync(CodingJob Job, string RequestKey, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("""
            INSERT INTO app.coding_jobs(id,actor,request_key,state,deadline,payload) VALUES(@id,@actor,@key,'Queued',@deadline,@payload)
            ON CONFLICT(actor,request_key) DO NOTHING;
            SELECT payload,state,requests FROM app.coding_jobs WHERE actor=@actor AND request_key=@key
            """, C);
        Q.Parameters.AddWithValue("id", Job.Id); Q.Parameters.AddWithValue("actor", Job.ActorId);
        Q.Parameters.AddWithValue("key", RequestKey); Q.Parameters.AddWithValue("deadline", Job.Deadline);
        Q.Parameters.AddWithValue("payload", JsonSerializer.Serialize(Job, Json));
        await using var R = await Q.ExecuteReaderAsync(Token); await R.ReadAsync(Token);
        return Read(R);
    }

    public async Task<CodingJob?> GetAsync(Guid Id, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("SELECT payload,state,requests FROM app.coding_jobs WHERE id=@id", C);
        Q.Parameters.AddWithValue("id", Id); await using var R = await Q.ExecuteReaderAsync(Token);
        return await R.ReadAsync(Token) ? Read(R) : null;
    }

    public async Task<IReadOnlyList<CodingJob>> ListAsync(string Actor, string Subject, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("SELECT (payload::jsonb - 'artifact')::text,state,requests FROM app.coding_jobs WHERE actor=@actor AND payload::jsonb->>'subject'=@subject ORDER BY deadline DESC LIMIT 200", C);
        Q.Parameters.AddWithValue("actor", Actor); Q.Parameters.AddWithValue("subject", Subject);
        await using var R = await Q.ExecuteReaderAsync(Token); var Jobs = new List<CodingJob>();
        while (await R.ReadAsync(Token)) Jobs.Add(Read(R));
        return Jobs;
    }

    public async Task<bool> ClaimAsync(Guid Id, string Capability, string RailwayToken, string Environment, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var T = await C.BeginTransactionAsync(Token);
        await using var Lock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(947120041)", C, T);
        await Lock.ExecuteNonQueryAsync(Token);
        await using var Q = new NpgsqlCommand("""
            UPDATE app.coding_jobs SET state='Running',capability_hash=@hash,lease_credential=@credential,lease_environment=@environment
            WHERE id=@id AND state='Queued' AND deadline>now()
            AND NOT EXISTS(SELECT 1 FROM app.coding_jobs WHERE state='Running')
            """, C, T);
        Q.Parameters.AddWithValue("id", Id); Q.Parameters.AddWithValue("hash", Hash(Capability));
        Q.Parameters.AddWithValue("credential", Protect(RailwayToken)); Q.Parameters.AddWithValue("environment", Environment);
        var Claimed = await Q.ExecuteNonQueryAsync(Token) == 1;
        await T.CommitAsync(Token);
        return Claimed;
    }

    public async Task<bool> SaveAsync(CodingJob Job, string ExpectedState, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("""
            UPDATE app.coding_jobs SET state=@state,
                payload=(@payload::jsonb || jsonb_build_object('sandboxId',payload::jsonb->'sandboxId','cleanupState',payload::jsonb->'cleanupState'))::text
            WHERE id=@id AND state=@expected
            """, C);
        Q.Parameters.AddWithValue("id", Job.Id); Q.Parameters.AddWithValue("state", Job.Status);
        Q.Parameters.AddWithValue("expected", ExpectedState); Q.Parameters.AddWithValue("payload", JsonSerializer.Serialize(Job, Json));
        return await Q.ExecuteNonQueryAsync(Token) == 1;
    }

    public async Task<bool> ReserveModelRequestAsync(Guid Id, string Capability, int Maximum, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("""
            UPDATE app.coding_jobs SET requests=requests+1 WHERE id=@id AND capability_hash=@hash
            AND state='Running' AND deadline>now() AND requests<@max
            """, C);
        Q.Parameters.AddWithValue("id", Id); Q.Parameters.AddWithValue("hash", Hash(Capability)); Q.Parameters.AddWithValue("max", Maximum);
        return await Q.ExecuteNonQueryAsync(Token) == 1;
    }

    public async Task<(string Credential, string Environment)> LeaseAsync(Guid Id, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("SELECT lease_credential,lease_environment FROM app.coding_jobs WHERE id=@id", C);
        Q.Parameters.AddWithValue("id", Id); await using var R = await Q.ExecuteReaderAsync(Token); await R.ReadAsync(Token);
        return (Unprotect(R.GetString(0)), R.GetString(1));
    }

    public async Task ClearLeaseAsync(Guid Id, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("UPDATE app.coding_jobs SET lease_credential='',capability_hash='',payload=jsonb_set(payload::jsonb,'{cleanupState}','\"Destroyed\"')::text WHERE id=@id", C);
        Q.Parameters.AddWithValue("id", Id); await Q.ExecuteNonQueryAsync(Token);
    }

    public async Task AttachSandboxAsync(Guid Id, string SandboxId, CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("UPDATE app.coding_jobs SET payload=jsonb_set(jsonb_set(payload::jsonb,'{sandboxId}',to_jsonb(@sandbox::text)),'{cleanupState}','\"Pending\"')::text WHERE id=@id", C);
        Q.Parameters.AddWithValue("id", Id); Q.Parameters.AddWithValue("sandbox", SandboxId);
        await Q.ExecuteNonQueryAsync(Token);
    }

    public async Task<IReadOnlyList<CodingJob>> PendingAsync(CancellationToken Token)
    {
        await using var C = await Database.OpenAsync(Token);
        await using var Q = new NpgsqlCommand("SELECT payload,state,requests FROM app.coding_jobs WHERE state NOT IN ('PrOpened','Failed','Cancelled') OR payload::jsonb->>'cleanupState'='Pending' ORDER BY deadline LIMIT 200", C);
        await using var R = await Q.ExecuteReaderAsync(Token); var Jobs = new List<CodingJob>();
        while (await R.ReadAsync(Token)) Jobs.Add(Read(R));
        return Jobs;
    }

    public Task<NpgsqlConnection> OpenAsync(CancellationToken Token) => Database.OpenAsync(Token);
    private static CodingJob Read(NpgsqlDataReader R) => JsonSerializer.Deserialize<CodingJob>(R.GetString(0), Json)! with { Status = R.GetString(1), ModelRequests = R.GetInt32(2) };
    private static string Hash(string S) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(S)));
    private string Protect(string S) => S.Length == 0 ? "" : PostgresConfigurationCrypto.Encrypt(S, "Shared", "Coding:Credentials", Database.EncryptionKey);
    private string Unprotect(string S) => S.Length == 0 ? "" : PostgresConfigurationCrypto.Decrypt(S, "Shared", "Coding:Credentials", Database.EncryptionKey);
    private static string EditSecret(string Action, string? Value, string Current) => Action switch
    {
        "keep" when string.IsNullOrEmpty(Value) => Current,
        "clear" when string.IsNullOrEmpty(Value) => "",
        "replace" when !string.IsNullOrWhiteSpace(Value) && Value.Length <= 16384 => Value,
        _ => throw new ArgumentException("Choose keep, replace or clear for credentials.")
    };
}
