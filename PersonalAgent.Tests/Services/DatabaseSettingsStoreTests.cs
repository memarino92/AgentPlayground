using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Integrations;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class DatabaseSettingsStoreTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRows_AreMasked_Editable_AndProtectedFromExternalConflicts(bool HasTimestamp)
    {
        var key = Convert.ToBase64String(new byte[32]);
        var database = new IntegrationDatabase(Fixture.ConnectionString, key);
        var store = new DatabaseSettingsStore(database);
        await using var connection = await database.OpenAsync(default);
        await using var initialize = new NpgsqlCommand($"""
            CREATE SCHEMA IF NOT EXISTS app;
            DROP TABLE IF EXISTS app.configuration_settings;
            CREATE TABLE app.configuration_settings(scope text, key text, value text NOT NULL,
                is_secret boolean NOT NULL, is_active boolean NOT NULL, PRIMARY KEY(scope,key)
                {(HasTimestamp ? ", updated_at timestamptz NOT NULL DEFAULT now()" : "")});
            INSERT INTO app.configuration_settings(scope,key,value,is_secret,is_active) VALUES
                ('Api','Custom:Token',@secret,true,true),
                ('Shared','Custom:Unknown','original',false,true),
                ('Worker','Custom:Unknown','worker',false,false);
            """, connection);
        initialize.Parameters.AddWithValue("secret", PostgresConfigurationCrypto.Encrypt("hidden-original", "Api", "Custom:Token", key));
        await initialize.ExecuteNonQueryAsync();
        var rows = await store.ReadAsync(default);
        rows.Should().HaveCount(3);
        rows.Single(Row => Row.IsSecret).Value.Should().BeNull();
        rows.Single(Row => Row.Scope == "Worker").IsActive.Should().BeFalse();
        var secret = rows.Single(Row => Row.IsSecret);
        var shared = rows.Single(Row => Row.Scope == "Shared");
        await store.SaveAsync(new([new(secret.Scope, secret.Key, secret.Version, null, false),
            new(shared.Scope, shared.Key, shared.Version, "updated", true)]), default);
        await using var readSecret = new NpgsqlCommand("SELECT value FROM app.configuration_settings WHERE is_secret", connection);
        var ciphertext = (string)(await readSecret.ExecuteScalarAsync())!;
        PostgresConfigurationCrypto.Decrypt(ciphertext, secret.Scope, secret.Key, key).Should().Be("hidden-original");
        rows = await store.ReadAsync(default);
        secret = rows.Single(Row => Row.IsSecret);
        shared = rows.Single(Row => Row.Scope == "Shared");
        await using var external = new NpgsqlCommand("UPDATE app.configuration_settings SET value = 'external' WHERE scope = 'Shared'", connection);
        await external.ExecuteNonQueryAsync();
        // Api sorts first: its update must roll back when the later Shared row conflicts.
        await ((Func<Task>)(() => store.SaveAsync(new([new(secret.Scope, secret.Key, secret.Version, "should-rollback", true),
            new(shared.Scope, shared.Key, shared.Version, "stale", true)]), default))).Should().ThrowAsync<IntegrationConflictException>();
        ((string)(await readSecret.ExecuteScalarAsync())!).Should().Be(ciphertext);
        await store.SaveAsync(new([new(secret.Scope, secret.Key, secret.Version, "replacement", true)]), default);
        ciphertext = (string)(await readSecret.ExecuteScalarAsync())!;
        ciphertext.Should().StartWith("v1:").And.NotContain("replacement");
        PostgresConfigurationCrypto.Decrypt(ciphertext, secret.Scope, secret.Key, key).Should().Be("replacement");
        secret = (await store.ReadAsync(default)).Single(Row => Row.IsSecret);
        await store.SaveAsync(new([new(secret.Scope, secret.Key, secret.Version, "", true)]), default);
        PostgresConfigurationCrypto.Decrypt((string)(await readSecret.ExecuteScalarAsync())!, secret.Scope, secret.Key, key).Should().BeEmpty();
        (await store.ReadAsync(default)).Single(Row => Row.Scope == "Worker").Value.Should().Be("worker");
        (await store.ReadAsync(default)).Single(Row => Row.Scope == "Shared").Value.Should().Be("external");
    }

    [Fact]
    public async Task InvalidBatch_IsRejectedBeforeOpeningDatabase()
    {
        var store = new DatabaseSettingsStore(new("", ""));
        var edit = new DatabaseSettingEdit("Api", "Key", "1", "value", true);
        foreach (var request in new[] { new SaveDatabaseSettingsRequest([]), new([edit, edit]), new([edit with { Value = new string('x', 262145) }]), new([edit with { Scope = "" }]) })
            await ((Func<Task>)(() => store.SaveAsync(request, default))).Should().ThrowAsync<IntegrationValidationException>();
    }
}
