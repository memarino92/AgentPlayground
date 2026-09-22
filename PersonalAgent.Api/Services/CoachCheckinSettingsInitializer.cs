using PersonalAgent.Integrations;
using Npgsql;

namespace PersonalAgent.Api.Services;

internal sealed class CoachCheckinSettingsInitializer(IntegrationDatabase Database) : IHostedService
{
    public async Task StartAsync(CancellationToken CancellationToken)
    {
        await using var Connection = await Database.OpenAsync(CancellationToken);
        await using var Transaction = await Connection.BeginTransactionAsync(CancellationToken);
        await using var Command = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'app' AND table_name = 'configuration_settings' AND column_name = 'updated_at')
            """, Connection, Transaction);
        var HasTimestamp = (bool)(await Command.ExecuteScalarAsync(CancellationToken))!;
        foreach (var (Key, Value) in new[]
        {
            ("CoachCheckins:CoachName", "Andrew"),
            ("CoachCheckins:AthleteName", "Michael")
        })
        {
            Command.CommandText = $"""
                INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active{(HasTimestamp ? ", updated_at" : "")})
                SELECT 'Api', @key, @value, false, true{(HasTimestamp ? ", now()" : "")}
                WHERE NOT EXISTS(SELECT 1 FROM app.configuration_settings
                    WHERE scope IN ('Shared', 'Api') AND lower(key) = lower(@key))
                ON CONFLICT DO NOTHING
                """;
            Command.Parameters.Clear();
            Command.Parameters.AddWithValue("key", Key);
            Command.Parameters.AddWithValue("value", Value);
            await Command.ExecuteNonQueryAsync(CancellationToken);
        }
        await Transaction.CommitAsync(CancellationToken);
    }

    public Task StopAsync(CancellationToken CancellationToken) => Task.CompletedTask;
}
