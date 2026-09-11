using AgentPlayground.Contracts.Configuration;
using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class DatabaseCredentialReloadTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task Reload_UpdatesLiveOptionsAndClients_PreservesStartupValues_AndRejectsInvalidBatch()
    {
        var key = Convert.ToBase64String(new byte[32]);
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using var initialize = new NpgsqlCommand("""
            CREATE SCHEMA IF NOT EXISTS app;
            CREATE TABLE app.configuration_settings(scope text,key text,value text,is_secret boolean,is_active boolean);
            INSERT INTO app.configuration_settings VALUES
                ('Shared','OpenAI:ApiKey','shared-original',false,true),
                ('Api','OpenAI:ApiKey',@secret,true,true),
                ('Api','AssemblyAi:ApiKey','assembly-original',false,true),
                ('Api','Security:InternalApiKey','internal-original',false,true);
            """, connection);
        initialize.Parameters.AddWithValue("secret", PostgresConfigurationCrypto.Encrypt("api-original", "Api", "OpenAI:ApiKey", key));
        await initialize.ExecuteNonQueryAsync();
        var provider = new PostgresConfigurationProvider(Fixture.ConnectionString, key, "Api");
        using var configuration = new ConfigurationRoot([provider]);
        var services = new ServiceCollection();
        services.AddOptions<ApiKeyOptions>().Configure(Options =>
        {
            Options.OpenAiKey = configuration["OpenAI:ApiKey"]!;
            Options.InternalApiKey = configuration["Security:InternalApiKey"]!;
        });
        services.AddLiveOptions<ApiKeyOptions>(configuration);
        using var container = services.BuildServiceProvider();
        var options = container.GetRequiredService<IOptions<ApiKeyOptions>>();
        var clients = new OpenAiClientProvider(options);
        var original = clients.Current;
        options.Value.OpenAiKey.Should().Be("api-original");
        await using var change = new NpgsqlCommand("""
            UPDATE app.configuration_settings SET value='api-next',is_secret=false WHERE scope='Api' AND key='OpenAI:ApiKey';
            UPDATE app.configuration_settings SET value='internal-next' WHERE key='Security:InternalApiKey';
            """, connection);
        await change.ExecuteNonQueryAsync();
        (await provider.ReloadCredentialsAsync(default)).Should().BeTrue();
        options.Value.OpenAiKey.Should().Be("api-next");
        options.Value.InternalApiKey.Should().Be("internal-original");
        clients.Current.Should().NotBeSameAs(original);
        var next = clients.Current;
        clients.Current.Should().BeSameAs(next);
        (await provider.ReloadCredentialsAsync(default)).Should().BeFalse();

        await using var invalid = new NpgsqlCommand("""
            UPDATE app.configuration_settings SET value='should-not-apply' WHERE key='OpenAI:ApiKey';
            UPDATE app.configuration_settings SET value='invalid key' WHERE key='AssemblyAi:ApiKey';
            """, connection);
        await invalid.ExecuteNonQueryAsync();
        await ((Func<Task>)(() => provider.ReloadCredentialsAsync(default))).Should().ThrowAsync<InvalidOperationException>();
        configuration["OpenAI:ApiKey"].Should().Be("api-next");
        configuration["AssemblyAi:ApiKey"].Should().Be("assembly-original");
        clients.Current.Should().BeSameAs(next);
        var runtime = new DatabaseCredentialRuntime(configuration);
        var status = await runtime.ReloadAsync(default);
        status.Status.Should().Contain("failed").And.NotContain("invalid key");
        status.LastChecked.Should().NotBeNull();

        await using var deactivate = new NpgsqlCommand("""
            UPDATE app.configuration_settings SET is_active=false WHERE scope='Api' AND key='OpenAI:ApiKey';
            UPDATE app.configuration_settings SET value='assembly-next' WHERE key='AssemblyAi:ApiKey';
            """, connection);
        await deactivate.ExecuteNonQueryAsync();
        (await runtime.ReloadAsync(default)).Status.Should().Contain("checked");
        options.Value.OpenAiKey.Should().Be("should-not-apply"); // The Shared override becomes effective.
        configuration["AssemblyAi:ApiKey"].Should().Be("assembly-next");
        options.Value.InternalApiKey.Should().Be("internal-original");
    }

    [Fact]
    public async Task ReloadWithoutDatabaseBootstrap_IsNotReportedAsApplied()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();
        var status = await new DatabaseCredentialRuntime(configuration).ReloadAsync(default);
        status.Status.Should().Contain("bootstrap is required");
    }
}
