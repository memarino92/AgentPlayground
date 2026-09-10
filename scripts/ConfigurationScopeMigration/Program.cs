using System.Security.Cryptography;
using AgentPlayground.Contracts.Messaging;
using ConfigurationScopeMigration;
using Npgsql;

try
{
    if (args.Any(Argument => Argument is not ("--apply" or "--keep-source")))
        throw new InvalidOperationException("Use --apply to migrate and optionally --keep-source for staged rollout.");
    var Apply = args.Contains("--apply");
    var KeepSource = args.Contains("--keep-source");
    var DatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    var MasterKey = Environment.GetEnvironmentVariable("CONFIG_ENCRYPTION_KEY");
    if (string.IsNullOrWhiteSpace(DatabaseUrl) || string.IsNullOrWhiteSpace(MasterKey))
        throw new InvalidOperationException("Set DATABASE_URL and CONFIG_ENCRYPTION_KEY, or pass -ValuesPath to the PowerShell script.");
    if (Convert.FromBase64String(MasterKey).Length != 32)
        throw new InvalidOperationException("CONFIG_ENCRYPTION_KEY must decode to 32 bytes.");

    await using var Connection = new NpgsqlConnection(PostgresConnectionStringNormalizer.Normalize(DatabaseUrl));
    await Connection.OpenAsync();
    await using var Transaction = await Connection.BeginTransactionAsync();
    // Serialize configuration writes, including insertion of an initially absent destination.
    // Reads by running services remain available. The transaction is short and bounded.
    await using (var Lock = new NpgsqlCommand("SET LOCAL lock_timeout = '5s'; LOCK TABLE app.configuration_settings IN SHARE ROW EXCLUSIVE MODE", Connection, Transaction))
        await Lock.ExecuteNonQueryAsync();
    var Settings = new List<Setting>();
    await using (var Select = new NpgsqlCommand("SELECT scope, key, value, is_secret, is_active FROM app.configuration_settings WHERE scope IN ('Worker', 'Api') AND key LIKE 'AssemblyAi:%'", Connection, Transaction))
    await using (var Reader = await Select.ExecuteReaderAsync())
        while (await Reader.ReadAsync()) Settings.Add(new(Reader.GetString(0), Reader.GetString(1), Reader.GetString(2), Reader.GetBoolean(3), Reader.GetBoolean(4)));

    var Moves = MigrationPlan.Create(Settings, MasterKey);
    if (!Apply)
    {
        await Transaction.RollbackAsync();
        Console.WriteLine($"Preview: {Moves.Count} Worker AssemblyAi setting(s); {Moves.Count(Move => Move.DestinationExists)} matching Api setting(s). Source retention: {KeepSource}. No database changes made. Use -Apply to migrate.");
        return 0;
    }
    foreach (var Move in Moves)
    {
        if (KeepSource && Move.DestinationExists) continue;
        var Sql = KeepSource
            ? "INSERT INTO app.configuration_settings (scope, key, value, is_secret, is_active, updated_at) SELECT 'Api', key, @value, is_secret, is_active, now() FROM app.configuration_settings WHERE scope = 'Worker' AND key = @key"
            : Move.DestinationExists
            ? "DELETE FROM app.configuration_settings WHERE scope = 'Worker' AND key = @key"
            : "UPDATE app.configuration_settings SET scope = 'Api', value = @value, updated_at = now() WHERE scope = 'Worker' AND key = @key";
        await using var Command = new NpgsqlCommand(Sql, Connection, Transaction);
        Command.Parameters.AddWithValue("key", Move.Source.Key);
        if (!Move.DestinationExists) Command.Parameters.AddWithValue("value", Move.EncryptedValue);
        if (await Command.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("Unexpected row count. Migration rolled back.");
    }
    await Transaction.CommitAsync();
    Console.WriteLine($"Verified {Moves.Count} AssemblyAi setting(s) in Api scope. Worker settings retained: {KeepSource}. Restart API when deploying the refactor.");
    return 0;
}
catch (CryptographicException)
{
    Console.Error.WriteLine("Configuration authentication failed. Verify the master key and original scopes. No migration committed.");
}
catch (PostgresException Error)
{
    Console.Error.WriteLine($"Database operation failed (SQLSTATE {Error.SqlState}). No migration committed.");
}
catch (InvalidOperationException Error)
{
    // Only our own validation messages are safe to report; library errors may contain credentials.
    Console.Error.WriteLine(Error.TargetSite?.DeclaringType?.Assembly == typeof(MigrationPlan).Assembly
        ? Error.Message : "Migration failed validation. No migration committed.");
}
catch (Exception)
{
    Console.Error.WriteLine("Migration failed. Check database connectivity and bootstrap configuration. Commit outcome may be uncertain if the connection was lost; rerun preview. No values have been printed.");
}
return 1;
