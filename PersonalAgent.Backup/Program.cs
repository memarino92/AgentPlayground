using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Contracts.Messaging;
using Renci.SshNet;

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
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromHours(2));
        try { await dump.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { dump.Kill(entireProcessTree: true); throw new InvalidOperationException("pg_dump timed out."); }
        if (dump.ExitCode != 0 || new FileInfo(archivePath).Length == 0)
            throw new InvalidOperationException("pg_dump failed or produced an empty archive.");

        using var client = new SftpClient(host, port, username, password);
        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(30);
        client.OperationTimeout = TimeSpan.FromMinutes(30);
        client.HostKeyReceived += (_, e) => e.CanTrust = string.Equals(e.FingerPrintSHA256.TrimEnd('='), fingerprint, StringComparison.Ordinal);
        client.Connect();
        var remote = $"{directory.TrimEnd('/')}/{archiveName}";
        var temporary = remote + ".partial";
        using (var stream = File.OpenRead(archivePath)) client.UploadFile(stream, temporary, canOverride: false);
        client.RenameFile(temporary, remote);
        client.Disconnect();

        await using var update = new NpgsqlCommand("UPDATE app.database_backup_state SET last_success_at = now() WHERE id = 1", connection);
        await update.ExecuteNonQueryAsync();
        Console.WriteLine("Database backup uploaded successfully.");
    }
    finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    return 0;

    string Required(string key) => configuration[$"Backup:Sftp:{key}"] is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Backup setting {key} is missing.");
}
catch (Exception)
{
    Console.Error.WriteLine("Database backup failed. Check configuration, PostgreSQL access, and the SFTP destination.");
    return 1;
}
