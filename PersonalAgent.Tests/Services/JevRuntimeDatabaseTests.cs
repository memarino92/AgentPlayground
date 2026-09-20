using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class JevRuntimeDatabaseTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeedAndReload_PreserveEditsEncryptKeysAndRetainValidSnapshot(bool HasTimestamp)
    {
        var Database = new IntegrationDatabase(Fixture.ConnectionString, Convert.ToBase64String(new byte[32]));
        await using var Connection = await Database.OpenAsync(default);
        await using var Command = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS app;
            DROP TABLE IF EXISTS app.configuration_settings;
            CREATE TABLE app.configuration_settings (
                scope text NOT NULL, key text NOT NULL, value text NOT NULL,
                is_secret boolean NOT NULL, is_active boolean NOT NULL,
                {(HasTimestamp ? "updated_at timestamptz NOT NULL," : "")}
                PRIMARY KEY(scope,key));
            """, Connection);
        await Command.ExecuteNonQueryAsync();
        using var Runtime = new JevRoutingRuntime(Database, NullLogger<JevRoutingRuntime>.Instance);
        await Runtime.InitializeAsync(default);
        await Runtime.ReloadAsync(default);
        Runtime.Current.CanCall.Should().BeFalse();
        var Store = new DatabaseSettingsStore(Database);
        var Rows = await Store.ReadAsync(default);
        var Key = Rows.Single(Row => Row.Key == "Jev:ApiKey");
        Key.IsSecret.Should().BeTrue();
        Key.Value.Should().BeNull();
        var Settings = Rows.Single(Row => Row.Key == "Jev:Settings");
        await Store.SaveAsync(new([
            new(Key.Scope, Key.Key, Key.Version, "synthetic-key", true),
            new(Settings.Scope, Settings.Key, Settings.Version, "{\"mode\":\"Suggest\",\"allowUserContent\":true}", true)]), default);
        if (HasTimestamp)
        {
            Command.CommandText = "UPDATE app.configuration_settings SET updated_at = '2026-01-01T00:00:00Z'";
            await Command.ExecuteNonQueryAsync();
        }
        await Runtime.InitializeAsync(default); // Insert-only seeding must not overwrite settings or keys.
        if (HasTimestamp)
        {
            Command.CommandText = "SELECT count(*) FROM app.configuration_settings WHERE updated_at = '2026-01-01T00:00:00Z'";
            ((long)(await Command.ExecuteScalarAsync())!).Should().Be(2);
        }
        await Runtime.ReloadAsync(default);
        Runtime.Current.CanCall.Should().BeTrue();
        Runtime.Current.ApiKey.Should().Be("synthetic-key");
        Command.CommandText = "SELECT value FROM app.configuration_settings WHERE key = 'Jev:ApiKey'";
        ((string)(await Command.ExecuteScalarAsync())!).Should().StartWith("v1:").And.NotContain("synthetic-key");
        Command.CommandText = "UPDATE app.configuration_settings SET value = 'invalid' WHERE key = 'Jev:Settings'";
        await Command.ExecuteNonQueryAsync();
        await Runtime.ReloadAsync(default);
        Runtime.Current.Settings.Mode.Should().Be(JevRoutingMode.Suggest);
        Command.CommandText = "UPDATE app.configuration_settings SET is_active = false WHERE key = 'Jev:Settings'";
        await Command.ExecuteNonQueryAsync();
        await Runtime.ReloadAsync(default);
        Runtime.Current.CanCall.Should().BeFalse();
    }
}
