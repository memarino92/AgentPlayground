using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Worker.Configuration;

namespace PersonalAgent.Worker.Services;

internal class CoachCallCleanupService(
    IOptions<SqlTransportOptions> sqlOptions,
    IOptions<CoachCheckinWorkerOptions> options,
    ILogger<CoachCallCleanupService> logger) : BackgroundService
{
    private readonly string _connectionString = sqlOptions.Value.ConnectionString ?? string.Empty;
    private readonly CoachCheckinWorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Coach check-in cleanup job failed");
            }

            await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
        }
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {CoachCallUploadsTable}
            SET audio_bytes = NULL,
                updated_at = @updatedAt
            WHERE status = 'Failed'
              AND updated_at < @cutoff
              AND audio_bytes IS NOT NULL;
            """;
        command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("cutoff", DateTimeOffset.UtcNow.AddDays(-_options.FailedUploadRetentionDays));
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Coach check-in cleanup removed staged audio from {RowCount} failed uploads", rows);
    }

    private string CoachCallUploadsTable => QualifiedTableName("coach_call_uploads");
    private string QualifiedTableName(string tableName) => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier(tableName)}";
    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
}
