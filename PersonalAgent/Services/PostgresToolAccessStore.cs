using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Models;

namespace PersonalAgent.Services;

internal sealed class PostgresToolAccessStore(IOptions<AgentMemoryOptions> options) : IToolAccessStore
{
    private readonly AgentMemoryOptions _options = options.Value;

    public async Task<IReadOnlyDictionary<string, bool>> GetRolePermissionsAsync(string role, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT tool_key, is_enabled FROM {TableName} WHERE role_name = @roleName;";
        command.Parameters.AddWithValue("roleName", role);

        var permissions = new Dictionary<string, bool>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) permissions[reader.GetString(0)] = reader.GetBoolean(1);
        return permissions;
    }

    public async Task SavePermissionsAsync(IReadOnlyList<ToolRolePermission> permissions, string updatedBy, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var permission in permissions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {TableName} (role_name, tool_key, is_enabled, updated_at, updated_by)
                VALUES (@roleName, @toolKey, @isEnabled, @updatedAt, @updatedBy)
                ON CONFLICT (role_name, tool_key)
                DO UPDATE SET is_enabled = EXCLUDED.is_enabled, updated_at = EXCLUDED.updated_at, updated_by = EXCLUDED.updated_by;
                """;
            command.Parameters.AddWithValue("roleName", permission.Role);
            command.Parameters.AddWithValue("toolKey", permission.ToolKey);
            command.Parameters.AddWithValue("isEnabled", permission.IsEnabled);
            command.Parameters.AddWithValue("updatedAt", DateTimeOffset.UtcNow);
            command.Parameters.AddWithValue("updatedBy", updatedBy);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private string TableName => $"{QuoteIdentifier(_options.Schema)}.{QuoteIdentifier("tool_role_permissions")}";
    private static string QuoteIdentifier(string identifier) => new NpgsqlCommandBuilder().QuoteIdentifier(identifier);
}
