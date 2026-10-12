using System.Text.Json;

using Npgsql;

using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Services;

internal sealed record RailwayLogService(string Name, string Id);
internal sealed record RailwayLogSettings(bool Enabled, string ProjectId, string EnvironmentId, string Environment,
    RailwayLogService[] Services, string Token);

internal interface IRailwayLogSettings
{
    Task<RailwayLogSettings> ReadAsync(CancellationToken Token);
}

// Read the exact Api rows on each invocation: removal/disablement fails closed, with no stale credential cache.
internal sealed class DatabaseRailwayLogSettings(IntegrationDatabase Database) : IRailwayLogSettings
{
    public async Task<RailwayLogSettings> ReadAsync(CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Command = new NpgsqlCommand("""
            SELECT key, value, is_secret FROM app.configuration_settings
            WHERE scope = 'Api' AND key IN ('RailwayLogs:Settings', 'RailwayLogs:ProjectToken') AND is_active
            """, Connection);
        await using var Reader = await Command.ExecuteReaderAsync(Token);
        var Values = new Dictionary<string, (string Value, bool Secret)>();
        while (await Reader.ReadAsync(Token)) Values[Reader.GetString(0)] = (Reader.GetString(1), Reader.GetBoolean(2));
        if (!Values.TryGetValue("RailwayLogs:Settings", out var Settings) || Settings.Secret)
            throw new InvalidOperationException("Railway log settings are missing.");
        var Result = JsonSerializer.Deserialize<RailwayLogSettings>(Settings.Value,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new JsonException();
        if (!Result.Enabled) return Result with { Token = "" };
        if (!Values.TryGetValue("RailwayLogs:ProjectToken", out var Credential) || !Credential.Secret)
            throw new InvalidOperationException("Railway project token must be configured as an encrypted secret.");
        return Result with { Token = PostgresConfigurationCrypto.Decrypt(Credential.Value, "Api", "RailwayLogs:ProjectToken", Database.EncryptionKey) };
    }
}
