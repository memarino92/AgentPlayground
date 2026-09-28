using System.Globalization;
using PersonalAgent.Contracts.Configuration;
using Npgsql;

namespace PersonalAgent.Integrations;

public sealed record BackupSettingsResponse(bool Enabled, string Host, int Port, string Username,
    bool HasPassword, string HostKeySha256, string Directory, int IntervalDays);

public sealed record SaveBackupSettingsRequest(bool Enabled, string Host, int Port, string Username,
    string? Password, string HostKeySha256, string Directory, int IntervalDays);

public sealed class BackupSettingsStore(IntegrationDatabase Database)
{
    private static readonly string[] Keys = ["Enabled", "Host", "Port", "Username", "Password", "HostKeySha256", "Directory", "IntervalDays"];

    public async Task<BackupSettingsResponse> ReadAsync(CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT key, value FROM app.configuration_settings WHERE scope = 'Backup' AND is_active", connection);
        Dictionary<string, string> values = [];
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        while (await reader.ReadAsync(CancellationToken)) values[reader.GetString(0)] = reader.GetString(1);
        string Get(string Key, string Default = "") => values.GetValueOrDefault($"Backup:Sftp:{Key}", Default);
        return new(Get("Enabled") == "true", Get("Host"), int.TryParse(Get("Port"), out var port) ? port : 22,
            Get("Username"), !string.IsNullOrEmpty(Get("Password")), Get("HostKeySha256"), Get("Directory", "/"),
            int.TryParse(Get("IntervalDays"), out var days) ? days : 1);
    }

    public async Task SaveAsync(SaveBackupSettingsRequest Request, CancellationToken CancellationToken)
    {
        var fingerprint = (Request.HostKeySha256 ?? "").Trim();
        if (fingerprint.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) fingerprint = fingerprint[7..];
        if (Request.Port is < 1 or > 65535 || Request.IntervalDays is < 1 or > 365
            || Uri.CheckHostName(Request.Host ?? "") == UriHostNameType.Unknown
            || string.IsNullOrWhiteSpace(Request.Username) || Request.Username.Length > 128
            || Request.Username.Any(char.IsControl) || Request.Password?.Length > 4096
            || string.IsNullOrEmpty(Request.Directory) || !Request.Directory.StartsWith('/') || Request.Directory.Contains("..", StringComparison.Ordinal)
            || Request.Directory.Any(char.IsControl) || Request.Directory.Length > 512
            || !System.Text.RegularExpressions.Regex.IsMatch(fingerprint, "^[A-Za-z0-9+/]{43}=?$"))
            throw new IntegrationValidationException(new() { ["Backup"] = ["Enter a valid host, port, user, absolute directory, 1–365 day interval, and SHA-256 SSH host key fingerprint."] });

        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        if (Request.Enabled && Request.Password is null)
        {
            await using var passwordCheck = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM app.configuration_settings
                    WHERE scope = 'Backup' AND key = 'Backup:Sftp:Password' AND is_active)
                """, connection, transaction);
            if (await passwordCheck.ExecuteScalarAsync(CancellationToken) is not true)
                throw new IntegrationValidationException(new() { ["Password"] = ["A password is required before enabling SFTP backups."] });
        }
        if (Request.Enabled && Request.Password is { Length: 0 })
            throw new IntegrationValidationException(new() { ["Password"] = ["A password is required before enabling SFTP backups."] });
        var values = new Dictionary<string, string>
        {
            ["Enabled"] = Request.Enabled ? "true" : "false", ["Host"] = Request.Host!.Trim(),
            ["Port"] = Request.Port.ToString(CultureInfo.InvariantCulture), ["Username"] = Request.Username.Trim(),
            ["HostKeySha256"] = fingerprint.TrimEnd('='), ["Directory"] = Request.Directory.TrimEnd('/') is { Length: > 0 } directory ? directory : "/",
            ["IntervalDays"] = Request.IntervalDays.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var key in Keys)
        {
            if (key == "Password" && Request.Password is null) continue;
            var value = key == "Password" ? PostgresConfigurationCrypto.Encrypt(Request.Password!, "Backup", "Backup:Sftp:Password", Database.EncryptionKey) : values[key];
            await using var command = new NpgsqlCommand("""
                INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active, updated_at)
                VALUES ('Backup', @key, @value, @secret, true, now())
                ON CONFLICT (scope, key) DO UPDATE SET value = excluded.value, is_secret = excluded.is_secret, is_active = true, updated_at = now()
                """, connection, transaction);
            command.Parameters.AddWithValue("key", $"Backup:Sftp:{key}");
            command.Parameters.AddWithValue("value", value);
            command.Parameters.AddWithValue("secret", key == "Password");
            await command.ExecuteNonQueryAsync(CancellationToken);
        }
        await transaction.CommitAsync(CancellationToken);
    }
}
