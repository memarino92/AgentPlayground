using AgentPlayground.Integrations;
using FluentAssertions;
using Npgsql;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class IntegrationSettingsStoreTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task Revisions_EncryptSecrets_RejectStaleEdits_AndApplyOnlyReviewedRevision()
    {
        var database = new IntegrationDatabase(Fixture.ConnectionString, Convert.ToBase64String(new byte[32]));
        await Task.WhenAll(database.MigrateAsync(default), database.MigrateAsync(default));
        var store = new IntegrationSettingsStore(database);
        var initial = await store.ReadAsync(false, default);
        var revision = await store.SaveAsync(new(initial.SavedRevision, IntegrationSettingsTests.Values()), "admin", default);
        (await store.ReadAsync(true, default)).Values["Enabled"].Should().Be("false");
        await ((Func<Task>)(() => store.SaveAsync(new(initial.SavedRevision, new()), "stale-admin", default))).Should().ThrowAsync<IntegrationConflictException>();
        await store.ApplyAsync(revision, "admin", default);
        var restart = new IntegrationSettingsStore(database);
        (await restart.ReadAsync(true, default)).Values["Dsn"].Should().Be(IntegrationSettingsTests.Dsn);
        var next = await restart.SaveAsync(new(revision, new() { ["Environment"] = "staging" }), "admin", default);
        await ((Func<Task>)(() => restart.ApplyAsync(revision, "admin", default))).Should().ThrowAsync<IntegrationConflictException>();
        (await restart.ReadAsync(true, default)).ActiveRevision.Should().Be(revision);
        await ((Func<Task>)(() => restart.SaveAsync(new(next, new() { ["Dsn"] = "" }), "admin", default))).Should().ThrowAsync<IntegrationValidationException>();
        (await restart.ReadAsync(false, default)).SavedRevision.Should().Be(next);

        using var runtime = new IntegrationRuntime(store, new IntegrationSettingsTests.FakeFactory(), new("Api"));
        var service = new IntegrationSettingsService(store, runtime);
        var publicSettings = await service.GetAsync(default);
        publicSettings.ConfiguredSecrets.Should().Contain("Dsn");
        publicSettings.Values.Should().NotContainKey("Dsn");
        await using var connection = await database.OpenAsync(default);
        await using var read = new NpgsqlCommand("SELECT encrypted_values FROM app.integration_revisions WHERE revision = @revision", connection);
        read.Parameters.AddWithValue("revision", next);
        ((string)(await read.ExecuteScalarAsync())!).Should().NotContain("0123456789abcdef").And.StartWith("v1:");

        await using var history = new NpgsqlCommand("SELECT applied_by FROM app.integration_applications WHERE revision = @revision", connection);
        history.Parameters.AddWithValue("revision", revision);
        (await history.ExecuteScalarAsync()).Should().Be("admin");

        var factory = new IntegrationSettingsTests.FakeFactory();
        using var web = new IntegrationRuntime(restart, factory, new("Web"));
        await web.ReloadAsync(default);
        await store.ApplyAsync(next, "admin", default);
        using var worker = new IntegrationRuntime(restart, factory, new("Worker"));
        await worker.ReloadAsync(default);
        await web.ReloadAsync(default);
        (await store.InstancesAsync(default)).Where(Instance => Instance.Service is "Web" or "Worker")
            .Should().HaveCount(2).And.OnlyContain(Instance => Instance.Revision == next);
    }
}
