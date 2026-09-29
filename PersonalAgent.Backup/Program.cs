using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Contracts.Messaging;
using Renci.SshNet;

var stage = "loading configuration";
var hostKeyMismatch = false;
try
{
    var configuration = new ConfigurationManager();
    configuration.AddEnvironmentVariables();
    configuration.AddPostgresConfiguration("Backup");
    if (!bool.TryParse(configuration["Backup:Sftp:Enabled"], out var enabled) || !enabled)
    {
        Console.WriteLine("Database backup is disabled.");
        return 0;
    }

    stage = "reading SFTP settings";
    var host = Required("Host");
    var username = Required("Username");
    var password = Required("Password");
    var fingerprint = Required("HostKeySha256").TrimEnd('=');
    var directory = Required("Directory");
    if (!int.TryParse(configuration["Backup:Sftp:Port"], out var port) || port is < 1 or > 65535
        || !int.TryParse(configuration["Backup:Sftp:IntervalDays"], out var intervalDays) || intervalDays is < 1 or > 365)
        throw new InvalidOperationException("Backup port or interval is invalid.");

    var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("DATABASE_URL is required.");
    var connectionString = PostgresConnectionStringNormalizer.Normalize(databaseUrl)!;
    stage = "connecting to PostgreSQL";
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    stage = "checking the backup schedule";
    await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(2049202601)", connection))
        if (await command.ExecuteScalarAsync() is not true)
        {
            Console.WriteLine("Another database backup is running.");
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
        if (last is DateTime lastSuccess && DateTime.UtcNow.Date - lastSuccess.ToUniversalTime().Date < TimeSpan.FromDays(intervalDays))
        {
            Console.WriteLine("Database backup is not due yet.");
            return 0;
        }
    }

    var archiveName = $"agentplayground-{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ssZ}-{Guid.NewGuid():N}.dump";
    var archivePath = Path.Combine(Path.GetTempPath(), archiveName);
    try
    {
        stage = "creating the database dump";
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        using var dump = new Process();
        dump.StartInfo.FileName = "pg_dump";
        dump.StartInfo.ArgumentList.Add("--format=custom");
        dump.StartInfo.ArgumentList.Add("--compress=zstd:9");
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
        var dumpErrorTask = dump.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        try { await dump.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { dump.Kill(entireProcessTree: true); throw new InvalidOperationException("pg_dump timed out."); }
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
            throw new InvalidOperationException($"pg_dump {failure} (exit code {dump.ExitCode}).");
        }
        if (new FileInfo(archivePath).Length == 0)
            throw new InvalidOperationException("pg_dump produced an empty archive.");

        stage = "connecting to SFTP";
        using var client = new SftpClient(host, port, username, password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        client.OperationTimeout = TimeSpan.FromMinutes(30);
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = string.Equals(e.FingerPrintSHA256.TrimEnd('='), fingerprint, StringComparison.Ordinal);
            hostKeyMismatch = !e.CanTrust;
        };
        client.Connect();
        stage = "preparing the SFTP backup directory";
        var currentDirectory = "";
        foreach (var segment in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentDirectory += "/" + segment;
            if (!client.Exists(currentDirectory))
            {
                client.CreateDirectory(currentDirectory);
                Console.WriteLine("Created a missing SFTP backup directory.");
            }
            if (!client.GetAttributes(currentDirectory).IsDirectory)
                throw new InvalidOperationException("The SFTP backup path contains a file instead of a directory.");
        }
        var remote = $"{directory.TrimEnd('/')}/{archiveName}";
        var temporary = remote + ".partial";
        stage = "uploading the database dump";
        using (var stream = File.OpenRead(archivePath)) client.UploadFile(stream, temporary, canOverride: false);
        stage = "finalizing the SFTP upload";
        client.RenameFile(temporary, remote);
        client.Disconnect();

        stage = "recording backup success";
        await using var update = new NpgsqlCommand("UPDATE app.database_backup_state SET last_success_at = now() WHERE id = 1", connection);
        await update.ExecuteNonQueryAsync();
        Console.WriteLine("Database backup uploaded successfully.");
    }
    finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    return 0;

    string Required(string key) => configuration[$"Backup:Sftp:{key}"] is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Backup setting {key} is missing.");
}
catch (Exception ex)
{
    var reason = hostKeyMismatch ? "SSH host key fingerprint mismatch" : ex is InvalidOperationException invalidOperation
        && invalidOperation.Message.StartsWith("pg_dump ", StringComparison.Ordinal)
            ? invalidOperation.Message : ex.GetType().Name;
    Console.Error.WriteLine($"Database backup failed during {stage}: {reason}.");
    return 1;
}
