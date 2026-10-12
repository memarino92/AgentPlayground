using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PersonalAgent.Backup;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Contracts.Messaging;
using PersonalAgent.Integrations;
using Renci.SshNet;

var diagnostics = new BackupDiagnostics();
diagnostics.Protect(Environment.GetEnvironmentVariable("DATABASE_URL"));
diagnostics.Protect(Environment.GetEnvironmentVariable("CONFIG_ENCRYPTION_KEY"));
diagnostics.Protect(Environment.GetEnvironmentVariable("SENTRY_DSN"));
var hostKeyMismatch = false;
try
{
    var configuration = new ConfigurationManager();
    configuration.AddEnvironmentVariables();
    diagnostics.Begin("loading Sentry configuration");
    try
    {
        using var settingsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var revision = await new IntegrationSettingsStore(IntegrationDatabase.FromConfiguration(configuration))
            .ReadAsync(true, settingsTimeout.Token);
        var (values, overrides) = IntegrationRegistry.ResolveOverrides(revision.Values, Environment.GetEnvironmentVariable);
        diagnostics.Protect(values.GetValueOrDefault("Dsn"));
        IntegrationRegistry.Validate(values);
        if (bool.Parse(values["Enabled"])) diagnostics.ErrorClient = new SentryErrorClientFactory().Create(values, "Backup");
        diagnostics.Info($"Sentry revision={revision.ActiveRevision} enabled={values["Enabled"]} sample_rate={values["SampleRate"]} overrides={string.Join(',', overrides)}");
    }
    catch (Exception exception)
    {
        diagnostics.Info($"Sentry initialization failed; backup will continue: {diagnostics.Clean(exception.ToString())}");
    }
    diagnostics.Begin("loading backup configuration");
    configuration.AddPostgresConfiguration("Backup");
    if (!bool.TryParse(configuration["Backup:Sftp:Enabled"], out var enabled) || !enabled)
    {
        diagnostics.Info("Database backup is disabled.");
        return 0;
    }

    diagnostics.Begin("reading SFTP settings");
    var host = Required("Host");
    var username = Required("Username");
    var password = Required("Password");
    diagnostics.Protect(password);
    var fingerprint = Required("HostKeySha256").TrimEnd('=');
    var directory = Required("Directory");
    if (!int.TryParse(configuration["Backup:Sftp:Port"], out var port) || port is < 1 or > 65535
        || !int.TryParse(configuration["Backup:Sftp:IntervalDays"], out var intervalDays) || intervalDays is < 1 or > 365)
        throw new InvalidOperationException("Backup port or interval is invalid.");

    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("DATABASE_URL is required.");
    var connectionString = PostgresConnectionStringNormalizer.Normalize(databaseUrl)!;
    diagnostics.Protect(connectionString);
    diagnostics.Protect(new NpgsqlConnectionStringBuilder(connectionString).Password);
    diagnostics.Context("sftp_host", host);
    diagnostics.Context("sftp_port", port);
    diagnostics.Info($"SFTP destination host={host} port={port} directory={directory} interval_days={intervalDays}; connect_timeout=30s operation_timeout=30m");
    diagnostics.Begin("connecting to PostgreSQL");
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    diagnostics.Begin("checking the backup schedule");
    await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(2049202601)", connection))
        if (await command.ExecuteScalarAsync() is not true)
        {
            diagnostics.Info("Another database backup is running.");
            return 0;
        }

    await using (var command = new NpgsqlCommand("""
        CREATE TABLE IF NOT EXISTS app.database_backup_state(
            id integer PRIMARY KEY CHECK (id = 1), last_success_at timestamptz);
        INSERT INTO app.database_backup_state(id) VALUES (1) ON CONFLICT DO NOTHING;
        """, connection))
        await command.ExecuteNonQueryAsync();
    await using (var command = new NpgsqlCommand("SELECT last_success_at FROM app.database_backup_state WHERE id = 1", connection))
    {
        var last = await command.ExecuteScalarAsync();
        diagnostics.Context("last_success_at", last is DateTime date ? date.ToUniversalTime().ToString("O") : "never");
        diagnostics.Info($"Last successful backup: {(last is DateTime previous ? previous.ToUniversalTime().ToString("O") : "never")}");
        if (last is DateTime lastSuccess && DateTime.UtcNow.Date - lastSuccess.ToUniversalTime().Date < TimeSpan.FromDays(intervalDays))
        {
            diagnostics.Info("Database backup is not due yet.");
            return 0;
        }
    }

    var archiveName = $"agentplayground-{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ssZ}-{Guid.NewGuid():N}.dump";
    var archivePath = Path.Combine(Path.GetTempPath(), archiveName);
    try
    {
        diagnostics.Begin("creating the database dump");
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        using var dump = new Process();
        dump.StartInfo.FileName = "pg_dump";
        dump.StartInfo.ArgumentList.Add("--format=custom");
        dump.StartInfo.ArgumentList.Add("--compress=zstd:9");
        dump.StartInfo.ArgumentList.Add("--verbose");
        dump.StartInfo.ArgumentList.Add($"--file={archivePath}");
        dump.StartInfo.ArgumentList.Add($"--dbname={builder.Database}");
        dump.StartInfo.Environment["PGHOST"] = builder.Host;
        dump.StartInfo.Environment["PGPORT"] = builder.Port.ToString();
        dump.StartInfo.Environment["PGUSER"] = builder.Username;
        dump.StartInfo.Environment["PGPASSWORD"] = builder.Password;
        dump.StartInfo.Environment["PGSSLMODE"] = builder.SslMode.ToString().ToLowerInvariant();
        dump.StartInfo.RedirectStandardError = true;
        dump.StartInfo.UseShellExecute = false;
        if (!dump.Start()) throw new InvalidOperationException("pg_dump did not start.");
        var dumpErrorTask = DrainDumpAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        try { await dump.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            dump.Kill(entireProcessTree: true);
            await dump.WaitForExitAsync();
            await dumpErrorTask;
            throw new InvalidOperationException("pg_dump timed out after two hours.");
        }
        var dumpError = await dumpErrorTask;
        if (dump.ExitCode != 0)
        {
            var failure = dumpError.Contains("server version mismatch", StringComparison.OrdinalIgnoreCase) ? "client/server version mismatch"
                : dumpError.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ? "database authentication failed"
                : dumpError.Contains("connection to server", StringComparison.OrdinalIgnoreCase) ? "database connection failed"
                : dumpError.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "permission denied"
                : dumpError.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ? "temporary disk full"
                : dumpError.Contains("unrecognized option", StringComparison.OrdinalIgnoreCase) ? "unsupported option"
                : "failed";
            throw new InvalidOperationException($"pg_dump {failure} (exit code {dump.ExitCode}). Recent output: {dumpError}");
        }
        if (new FileInfo(archivePath).Length == 0)
            throw new InvalidOperationException("pg_dump produced an empty archive.");

        diagnostics.Info($"Dump completed: {new FileInfo(archivePath).Length} bytes");
        SftpClient CreateClient()
        {
            var client = new SftpClient(host, port, username, password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
            client.OperationTimeout = TimeSpan.FromMinutes(30);
            client.KeepAliveInterval = TimeSpan.FromSeconds(15);
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = string.Equals(e.FingerPrintSHA256.TrimEnd('='), fingerprint, StringComparison.Ordinal);
                hostKeyMismatch |= !e.CanTrust;
                diagnostics.Info($"SSH host key algorithm={e.HostKeyName} trusted={e.CanTrust}");
            };
            return client;
        }
        var remote = $"{directory.TrimEnd('/')}/{archiveName}";
        using (var stream = File.OpenRead(archivePath)) BackupUpload.Run(CreateClient, stream, remote, diagnostics);

        diagnostics.Begin("recording backup success");
        await using var update = new NpgsqlCommand("UPDATE app.database_backup_state SET last_success_at = now() WHERE id = 1", connection);
        await update.ExecuteNonQueryAsync();
        diagnostics.Info("Database backup uploaded successfully.");

        async Task<string> DrainDumpAsync()
        {
            var recent = new Queue<string>();
            while (await dump.StandardError.ReadLineAsync() is { } line)
            {
                var safe = diagnostics.Clean(line);
                diagnostics.Info($"pg_dump: {safe}");
                recent.Enqueue(safe);
                if (recent.Count > 10) recent.Dequeue();
            }
            return string.Join(Environment.NewLine, recent);
        }
    }
    finally
    {
        try { if (File.Exists(archivePath)) File.Delete(archivePath); }
        catch (Exception exception) { diagnostics.Failure(exception, "Could not remove local temporary dump", 2); }
    }
    return 0;

    string Required(string key) => configuration[$"Backup:Sftp:{key}"] is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Backup setting {key} is missing.");
}
catch (Exception ex)
{
    diagnostics.Failure(ex, hostKeyMismatch ? "Database backup failed: SSH host key fingerprint mismatch" : "Database backup failed");
    return 1;
}
finally
{
    if (diagnostics.ErrorClient is { } client)
    {
        try { await client.FlushAsync(TimeSpan.FromSeconds(10)); }
        catch { diagnostics.Info("Sentry flush failed; preserving backup outcome"); }
        finally { try { client.Dispose(); } catch { /* Preserve backup exit code. */ } }
    }
}
