using System.Text.Json;

using Npgsql;

using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Services;

internal sealed record WhatboxConfiguration(string Host, string Username, string HostKeySha256, bool Enabled, string? PrivateKey,
    string FilesDirectory, string RTorrentSocketPath)
{
    public static WhatboxConfiguration Empty { get; } = new("", "", "", false, null, "~/files", "~/.config/rtorrent/socket");

    public WhatboxSettingsResponse ToResponse(string? Version) =>
        new(Version, Host, Username, HostKeySha256, Enabled, PrivateKey is not null, FilesDirectory, RTorrentSocketPath);
}

internal interface IWhatboxSettingsStore
{
    Task<(string? Version, WhatboxConfiguration Configuration)> ReadAsync(CancellationToken Token);
    Task<string> SaveAsync(string? Version, WhatboxConfiguration Configuration, CancellationToken Token);
}

internal sealed class WhatboxSettingsStore(IntegrationDatabase Database) : IWhatboxSettingsStore
{
    private const string Scope = "Whatbox";
    private const string Key = "Whatbox:Connection";

    public async Task<(string? Version, WhatboxConfiguration Configuration)> ReadAsync(CancellationToken Token)
    {
        await using var connection = await Database.OpenAsync(Token);
        await using var command = new NpgsqlCommand("""
            SELECT xmin::text, value FROM app.configuration_settings
            WHERE scope = 'Whatbox' AND key = 'Whatbox:Connection' AND is_secret AND is_active
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Token);
        if (!await reader.ReadAsync(Token)) return (null, WhatboxConfiguration.Empty);
        var json = PostgresConfigurationCrypto.Decrypt(reader.GetString(1), Scope, Key, Database.EncryptionKey);
        return (reader.GetString(0), JsonSerializer.Deserialize<WhatboxConfiguration>(json)
            ?? throw new InvalidOperationException("Whatbox settings are invalid."));
    }

    public async Task<string> SaveAsync(string? Version, WhatboxConfiguration Configuration, CancellationToken Token)
    {
        var encrypted = PostgresConfigurationCrypto.Encrypt(
            JsonSerializer.Serialize(Configuration), Scope, Key, Database.EncryptionKey);
        await using var connection = await Database.OpenAsync(Token);
        var sql = Version is null ? """
            INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active, updated_at)
            VALUES ('Whatbox', 'Whatbox:Connection', @value, true, true, now())
            ON CONFLICT (scope, key) DO NOTHING
            RETURNING xmin::text
            """ : """
            UPDATE app.configuration_settings SET value = @value, updated_at = now()
            WHERE scope = 'Whatbox' AND key = 'Whatbox:Connection' AND is_secret AND is_active AND xmin::text = @version
            RETURNING xmin::text
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("value", encrypted);
        if (Version is not null) command.Parameters.AddWithValue("version", Version);
        return await command.ExecuteScalarAsync(Token) as string ?? throw new IntegrationConflictException();
    }
}
