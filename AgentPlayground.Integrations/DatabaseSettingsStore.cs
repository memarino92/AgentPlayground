using AgentPlayground.Contracts.Configuration;
using Npgsql;

namespace AgentPlayground.Integrations;

public sealed record DatabaseSetting(string Scope, string Key, string Version, string? Value, bool IsSecret, bool IsActive)
{
    public bool SupportsLiveReload => LiveCredentialPolicy.Supports(Scope, Key);
}
public sealed record DatabaseSettingEdit(string Scope, string Key, string Version, string? Value, bool IsActive);
public sealed record SaveDatabaseSettingsRequest(List<DatabaseSettingEdit> Changes);

/// <summary>Edits startup configuration in place. Null values retain existing secrets.</summary>
public sealed class DatabaseSettingsStore(IntegrationDatabase Database)
{
    public async Task<List<DatabaseSetting>> ReadAsync(CancellationToken CancellationToken)
    {
        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT scope, key, xmin::text, CASE WHEN is_secret THEN NULL ELSE value END, is_secret, is_active
            FROM app.configuration_settings ORDER BY scope, key
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken);
        List<DatabaseSetting> result = [];
        while (await reader.ReadAsync(CancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task SaveAsync(SaveDatabaseSettingsRequest Request, CancellationToken CancellationToken)
    {
        if (Request.Changes is null || Request.Changes.Count is 0 or > 500
            || Request.Changes.Any(Edit => Edit is null || string.IsNullOrWhiteSpace(Edit.Scope)
                || string.IsNullOrWhiteSpace(Edit.Key) || string.IsNullOrWhiteSpace(Edit.Version)
                || Edit.Value?.Length > 262144)
            || Request.Changes.Select(Edit => (Edit.Scope, Edit.Key)).Distinct().Count() != Request.Changes.Count)
            throw new IntegrationValidationException(new() { ["Changes"] = ["Submit 1–500 distinct existing settings; values must be at most 262144 characters."] });

        if (Request.Changes.Any(Edit => LiveCredentialPolicy.Supports(Edit.Scope, Edit.Key)
            && Edit.Value is not null && !LiveCredentialPolicy.IsValid(Edit.Value)))
            throw new IntegrationValidationException(new() { ["Credentials"] = ["OpenAI and AssemblyAI keys must be nonempty printable tokens of at most 4096 characters."] });

        await using var connection = await Database.OpenAsync(CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken);
        // Older synthetic databases predate the production updated_at column.
        await using var schema = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
            WHERE table_schema = 'app' AND table_name = 'configuration_settings' AND column_name = 'updated_at')
            """, connection, transaction);
        var timestamp = (bool)(await schema.ExecuteScalarAsync(CancellationToken))! ? ", updated_at = now()" : "";
        foreach (var edit in Request.Changes.OrderBy(Edit => Edit.Scope, StringComparer.Ordinal).ThenBy(Edit => Edit.Key, StringComparer.Ordinal))
        {
            await using var read = new NpgsqlCommand("""
                SELECT is_secret FROM app.configuration_settings
                WHERE scope = @scope AND key = @key AND xmin::text = @version FOR UPDATE
                """, connection, transaction);
            read.Parameters.AddWithValue("scope", edit.Scope);
            read.Parameters.AddWithValue("key", edit.Key);
            read.Parameters.AddWithValue("version", edit.Version);
            var secret = await read.ExecuteScalarAsync(CancellationToken);
            if (secret is not bool isSecret) throw new IntegrationConflictException();
            var value = edit.Value is null ? null : isSecret
                ? PostgresConfigurationCrypto.Encrypt(edit.Value, edit.Scope, edit.Key, Database.EncryptionKey)
                : edit.Value;
            await using var update = new NpgsqlCommand($"""
                UPDATE app.configuration_settings SET value = COALESCE(@value, value), is_active = @active{timestamp}
                WHERE scope = @scope AND key = @key
                """, connection, transaction);
            update.Parameters.AddWithValue("scope", edit.Scope);
            update.Parameters.AddWithValue("key", edit.Key);
            update.Parameters.AddWithValue("active", edit.IsActive);
            update.Parameters.Add("value", NpgsqlTypes.NpgsqlDbType.Text).Value = (object?)value ?? DBNull.Value;
            await update.ExecuteNonQueryAsync(CancellationToken);
        }
        await transaction.CommitAsync(CancellationToken);
    }
}
