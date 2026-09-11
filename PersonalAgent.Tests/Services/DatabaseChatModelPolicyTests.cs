using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class DatabaseChatModelPolicyTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_IsInsertOnly_AndDoesNotRestoreDeletedModels(bool HasTimestamp)
    {
        var Database = await ResetAsync(HasTimestamp);
        var Policy = new DatabaseChatModelPolicy(Database);
        await Policy.InitializeAsync(default);
        (await Policy.ReadAsync(default)).Models.Select(Model => Model.Id).Should().Contain("gpt-6-astra");
        var Store = new DatabaseSettingsStore(Database);
        var Row = (await Store.ReadAsync(default)).Single(Value => Value.Key == "ChatModels:Models:0:Id");
        await Store.SaveAsync(new([new(Row.Scope, Row.Key, Row.Version, "owner-selected", true)]), default);
        await new DatabaseChatModelPolicy(Database).InitializeAsync(default);
        (await Policy.ReadAsync(default)).Models[0].Id.Should().Be("owner-selected");
        await ExecuteAsync("DELETE FROM app.configuration_settings WHERE key LIKE 'ChatModels:Models:%'");
        await new DatabaseChatModelPolicy(Database).InitializeAsync(default);
        (await Policy.ReadAsync(default)).Models.Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingPolicy_PreservesScopePrecedence_AndInactiveRowsPreventReseeding()
    {
        var Database = await ResetAsync(false);
        await ExecuteAsync("""
            INSERT INTO app.configuration_settings(scope,key,value,is_secret,is_active) VALUES
                ('Shared','ChatModels:Models:0:Id','shared-model',false,true),
                ('Shared','ChatModels:RefreshIntervalSeconds','42',false,true),
                ('Api','ChatModels:Models:0:Id','api-model',false,true),
                ('Api','ChatModels:Models:1:Id','disabled-model',false,false),
                ('Worker','ChatModels:Models:2:Id','worker-model',false,true);
            """);
        var Policy = new DatabaseChatModelPolicy(Database);
        await Policy.InitializeAsync(default);
        var Options = await Policy.ReadAsync(default);
        Options.Models.Should().ContainSingle().Which.Id.Should().Be("api-model");
        Options.RefreshIntervalSeconds.Should().Be(42);
        await ExecuteAsync("UPDATE app.configuration_settings SET is_active = false WHERE scope IN ('Shared','Api') AND key LIKE 'ChatModels:Models:%'");
        await new DatabaseChatModelPolicy(Database).InitializeAsync(default);
        (await Policy.ReadAsync(default)).Models.Should().BeEmpty();
    }

    [Fact]
    public async Task EditorChanges_InvalidateCachedInventory_WithoutFallbackToAppOptionsOrRemovedModels()
    {
        var Database = await ResetAsync(false);
        await ExecuteAsync("""
            INSERT INTO app.configuration_settings(scope,key,value,is_secret,is_active) VALUES
                ('Api','ChatModels:Models:0:Id','old-model',false,true);
            """);
        var Policy = new DatabaseChatModelPolicy(Database);
        await Policy.InitializeAsync(default);
        var Discovery = new Mock<IChatModelDiscovery>();
        Discovery.SetupSequence(Value => Value.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["old-model"])
            .ThrowsAsync(new HttpRequestException());
        using var Catalog = new ChatModelCatalog(Options.Create(new ChatModelCatalogOptions
        {
            Models = [new() { Id = "appsettings-model" }]
        }), Discovery.Object, TimeProvider.System, NullLogger<ChatModelCatalog>.Instance, Policy);
        (await Catalog.GetDefaultModelAsync()).Id.Should().Be("old-model");
        var Store = new DatabaseSettingsStore(Database);
        var Row = (await Store.ReadAsync(default)).Single(Value => Value.Key == "ChatModels:Models:0:Id");
        await Store.SaveAsync(new([new(Row.Scope, Row.Key, Row.Version, "new-model", true)]), default);
        (await Catalog.GetModelsAsync()).Should().ContainSingle().Which.Id.Should().Be("new-model");
        Discovery.Verify(Value => Value.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        Row = (await Store.ReadAsync(default)).Single(Value => Value.Key == "ChatModels:Models:0:Id");
        await Store.SaveAsync(new([new(Row.Scope, Row.Key, Row.Version, null, false)]), default);
        (await Catalog.GetModelsAsync()).Should().BeEmpty();
        await Catalog.Invoking(Value => Value.GetDefaultModelAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid")]
    public async Task InvalidPolicy_DoesNotServePreviouslyCachedModels(string Interval)
    {
        var Database = await ResetAsync(false);
        var Policy = new DatabaseChatModelPolicy(Database);
        await Policy.InitializeAsync(default);
        var Discovery = new Mock<IChatModelDiscovery>();
        Discovery.Setup(Value => Value.GetModelIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["gpt-6-astra"]);
        using var Catalog = new ChatModelCatalog(Options.Create(new ChatModelCatalogOptions()), Discovery.Object, TimeProvider.System,
            NullLogger<ChatModelCatalog>.Instance, Policy);
        (await Catalog.GetModelsAsync()).Should().ContainSingle();
        var Store = new DatabaseSettingsStore(Database);
        var Row = (await Store.ReadAsync(default)).Single(Value => Value.Key == "ChatModels:RefreshIntervalSeconds");
        await Store.SaveAsync(new([new(Row.Scope, Row.Key, Row.Version, Interval, true)]), default);
        await Catalog.Invoking(Value => Value.GetModelsAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    private async Task<IntegrationDatabase> ResetAsync(bool HasTimestamp)
    {
        await ExecuteAsync($"""
            CREATE SCHEMA IF NOT EXISTS app;
            DROP TABLE IF EXISTS app.configuration_settings;
            CREATE TABLE app.configuration_settings(scope text NOT NULL, key text NOT NULL, value text NOT NULL,
                is_secret boolean NOT NULL, is_active boolean NOT NULL DEFAULT true,
                {(HasTimestamp ? "updated_at timestamptz NOT NULL," : "")}
                PRIMARY KEY(scope,key));
            """);
        return new IntegrationDatabase(Fixture.ConnectionString, Convert.ToBase64String(new byte[32]));
    }

    private async Task ExecuteAsync(string Sql)
    {
        await using var Connection = new NpgsqlConnection(Fixture.ConnectionString);
        await Connection.OpenAsync();
        await using var Command = new NpgsqlCommand(Sql, Connection);
        await Command.ExecuteNonQueryAsync();
    }
}
