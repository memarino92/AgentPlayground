using Microsoft.Extensions.Options;
using Npgsql;
using AgentPlayground.Contracts.Messaging;

namespace PersonalAgent.Web.Services;

internal class DataProtectionKeyTableInitializer(IOptions<MessagingOptions> messagingOptions) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(messagingOptions.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        const string sql = """
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE IF NOT EXISTS app.data_protection_keys (
                friendly_name text PRIMARY KEY,
                xml text NOT NULL
            );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
