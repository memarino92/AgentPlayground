using System.Text.Json;
using System.Text.RegularExpressions;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;
using Npgsql;

namespace PersonalAgent.Api.Services;

internal enum JevRoutingMode { Off, Shadow, Suggest, DirectReadOnly, DirectTools }

internal sealed record JevRoutingSettings
{
    public JevRoutingMode Mode { get; init; }
    public string Model { get; init; } = "jev-1.13.0";
    public int TimeoutMilliseconds { get; init; } = 750;
    public double MinimumProbability { get; init; } = 0.95;
    public double MinimumConfidence { get; init; } = 0.8;
    public bool AllowUserContent { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Enum.IsDefined(Mode)
        && Model is { Length: > 0 and <= 64 }
        && Regex.IsMatch(Model, @"\Ajev-[0-9]+\.[0-9]+\.[0-9]+\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
        && TimeoutMilliseconds is >= 100 and <= 5000
        && double.IsFinite(MinimumProbability) && MinimumProbability is >= 0.5 and <= 1
        && double.IsFinite(MinimumConfidence) && MinimumConfidence is >= 0 and <= 1;
}

// Never include credentials in generated record ToString output.
internal sealed class JevRoutingSnapshot(JevRoutingSettings Settings, string ApiKey)
{
    public JevRoutingSettings Settings { get; } = Settings;
    public string ApiKey { get; } = ApiKey;
    public bool CanCall => Settings.Mode != JevRoutingMode.Off && Settings.AllowUserContent && ApiKey.Length > 0;
    public static JevRoutingSnapshot Disabled { get; } = new(new(), "");
}

internal interface IJevRoutingSettings { JevRoutingSnapshot Current { get; } }

internal sealed class JevRoutingRuntime(IntegrationDatabase Database, ILogger<JevRoutingRuntime> Logger)
    : BackgroundService, IJevRoutingSettings
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter<JevRoutingMode>() },
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private JevRoutingSnapshot _current = JevRoutingSnapshot.Disabled;
    public JevRoutingSnapshot Current => Volatile.Read(ref _current);

    internal static void ValidateEdits(SaveDatabaseSettingsRequest Request)
    {
        foreach (var Edit in Request.Changes ?? [])
        {
            if (Edit is null || Edit.Scope != "Api" || Edit.Value is null) continue;
            var Valid = true;
            if (Edit.Key == "Jev:Settings")
            {
                try { Valid = JsonSerializer.Deserialize<JevRoutingSettings>(Edit.Value, JsonOptions)?.IsValid == true; }
                catch (JsonException) { Valid = false; }
            }
            if (Edit.Key == "Jev:ApiKey") Valid = Edit.Value.Length <= 4096 && Edit.Value.All(Character => Character is >= '!' and <= '~');
            if (!Valid) throw new IntegrationValidationException(new() { [Edit.Key] = ["Invalid Jev setting. Check the routing mode, pinned model, thresholds, timeout and credential syntax."] });
        }
    }

    public override async Task StartAsync(CancellationToken CancellationToken)
    {
        await InitializeAsync(CancellationToken);
        await ReloadAsync(CancellationToken);
        await base.StartAsync(CancellationToken);
    }

    internal async Task InitializeAsync(CancellationToken Token)
    {
        await using var Connection = await Database.OpenAsync(Token);
        await using var Transaction = await Connection.BeginTransactionAsync(Token);
        await using var TimestampCommand = new NpgsqlCommand("""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'app' AND table_name = 'configuration_settings' AND column_name = 'updated_at')
            """, Connection, Transaction);
        var HasTimestamp = (bool)(await TimestampCommand.ExecuteScalarAsync(Token))!;
        foreach (var (Key, Value, Secret) in new[]
        {
            ("Jev:Settings", JsonSerializer.Serialize(new JevRoutingSettings(), JsonOptions), false),
            ("Jev:ApiKey", "", true)
        })
        {
            await using var Command = new NpgsqlCommand($"""
                INSERT INTO app.configuration_settings(scope, key, value, is_secret, is_active{(HasTimestamp ? ", updated_at" : "")})
                VALUES ('Api', @key, @value, @secret, true{(HasTimestamp ? ", now()" : "")}) ON CONFLICT DO NOTHING
                """, Connection, Transaction);
            Command.Parameters.AddWithValue("key", Key);
            Command.Parameters.AddWithValue("value", Secret
                ? PostgresConfigurationCrypto.Encrypt(Value, "Api", Key, Database.EncryptionKey) : Value);
            Command.Parameters.AddWithValue("secret", Secret);
            await Command.ExecuteNonQueryAsync(Token);
        }
        await Transaction.CommitAsync(Token);
    }

    internal async Task ReloadAsync(CancellationToken Token)
    {
        try
        {
            await using var Connection = await Database.OpenAsync(Token);
            await using var Command = new NpgsqlCommand("""
                SELECT key, value, is_secret FROM app.configuration_settings
                WHERE scope = 'Api' AND key IN ('Jev:Settings', 'Jev:ApiKey') AND is_active
                """, Connection);
            await using var Reader = await Command.ExecuteReaderAsync(Token);
            string? SettingsJson = null;
            var KeyValue = "";
            while (await Reader.ReadAsync(Token))
            {
                var Key = Reader.GetString(0);
                var Secret = Reader.GetBoolean(2);
                if (Key == "Jev:ApiKey" && !Secret) throw new InvalidOperationException("Jev credentials must be encrypted.");
                var Value = Secret ? PostgresConfigurationCrypto.Decrypt(Reader.GetString(1), "Api", Key, Database.EncryptionKey) : Reader.GetString(1);
                if (Key == "Jev:Settings") SettingsJson = Value;
                else KeyValue = Value;
            }
            var Settings = SettingsJson is null ? new() : JsonSerializer.Deserialize<JevRoutingSettings>(SettingsJson, JsonOptions);
            if (Settings is null || !Settings.IsValid || KeyValue.Length > 4096 || KeyValue.Any(Character => Character is < '!' or > '~'))
                throw new InvalidOperationException("Invalid Jev configuration.");
            Volatile.Write(ref _current, new(Settings, KeyValue));
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // No exception payload: JSON errors can contain configuration values.
            Logger.LogError(new EventId(2601), "Jev settings reload failed; retaining the last valid snapshot.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        using var Timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await Timer.WaitForNextTickAsync(StoppingToken)) await ReloadAsync(StoppingToken);
    }
}
