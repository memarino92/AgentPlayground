using AgentPlayground.Contracts.Configuration;

namespace ConfigurationScopeMigration;

internal record Setting(string Scope, string Key, string Value, bool IsSecret, bool IsActive);
internal record SettingMove(Setting Source, string EncryptedValue, bool DestinationExists);

internal static class MigrationPlan
{
    public static List<SettingMove> Create(IReadOnlyList<Setting> Settings, string MasterKey)
    {
        var Moves = new List<SettingMove>();
        foreach (var Source in Settings.Where(Row => Row.Scope == "Worker" && Row.Key.StartsWith("AssemblyAi:", StringComparison.Ordinal)))
        {
            var Plaintext = Read(Source, MasterKey);
            var Destination = Settings.SingleOrDefault(Row => Row.Scope == "Api" && Row.Key == Source.Key);
            if (Destination is not null && (Destination.IsSecret != Source.IsSecret || Destination.IsActive != Source.IsActive || Read(Destination, MasterKey) != Plaintext))
                throw new InvalidOperationException("An Api setting differs from its Worker counterpart. No settings were moved; resolve the conflict before retrying.");
            var Value = Source.IsSecret ? PostgresConfigurationCrypto.Encrypt(Plaintext, "Api", Source.Key, MasterKey) : Plaintext;
            Moves.Add(new(Source, Value, Destination is not null));
        }
        return Moves;
    }

    private static string Read(Setting Row, string MasterKey) => Row.IsSecret
        ? PostgresConfigurationCrypto.Decrypt(Row.Value, Row.Scope, Row.Key, MasterKey)
        : Row.Value;
}
