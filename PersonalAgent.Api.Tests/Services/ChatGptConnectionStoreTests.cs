using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Npgsql;
using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Configuration;
using PersonalAgent.Integrations;
using PersonalAgent.Integrations.ChatGpt;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class ChatGptConnectionStoreTests(PostgresVectorFixture Fixture) : IClassFixture<PostgresVectorFixture>
{
    private static readonly string Key = Convert.ToBase64String(new byte[32]);

    [Fact]
    public async Task Pairing_IsOneTime_Encrypted_AndScopedToOwner()
    {
        using var transport = new ChatGptTestTransport();
        using var http = new HttpClient(transport);
        var store = await ResetAsync(new ChatGptOAuth(http));
        var pairing = await store.StartAsync("owner-a", null, default);
        var attempt = await store.PrepareAsync(pairing.Code, default);
        await store.Invoking(Value => Value.PrepareAsync(pairing.Code, default)).Should().ThrowAsync<ChatGptException>();
        var credential = Credentials(transport, attempt);
        await store.CompleteAsync(pairing.Code, credential, default);
        await store.Invoking(Value => Value.CompleteAsync(pairing.Code, credential, default)).Should().ThrowAsync<ChatGptException>();
        var account = (await store.ListAsync("owner-a", default)).Should().ContainSingle().Which;
        account.PlanEnabled.Should().BeTrue();
        account.WelcomeRequired.Should().BeTrue();
        await store.DismissWelcomeAsync("owner-a", account.Id, default);
        (await store.ListAsync("owner-a", default)).Single().WelcomeRequired.Should().BeFalse();
        (await store.ListAsync("owner-b", default)).Should().BeEmpty();
        await store.Invoking(Value => Value.GetCredentialsAsync("owner-b", account.Id, default)).Should().ThrowAsync<ChatGptException>();
        var encrypted = (string)(await ExecuteScalarAsync("SELECT credentials FROM app.chatgpt_connections"))!;
        encrypted.Should().StartWith("v1:").And.NotContain(credential.AccessToken).And.NotContain(credential.RefreshToken!);
        JsonSerializer.Serialize(account).Should().NotContain("AccessToken").And.NotContain("IdToken");
    }

    [Fact]
    public async Task Reauthorization_ReusesHostAndClient_RejectsAnotherSubject_AndPreservesOldCredentials()
    {
        using var transport = new ChatGptTestTransport();
        using var http = new HttpClient(transport);
        var store = await ResetAsync(new ChatGptOAuth(http));
        var (account, initial) = await ConnectAsync(store, transport);
        var nextPairing = await store.StartAsync("owner-a", account.Id, default);
        var next = await store.PrepareAsync(nextPairing.Code, default);
        next.HostId.Should().Be(initial.HostId);
        next.ClientId.Should().Be("oaiapp_test");
        next.IdTokenHint.Should().NotBeNullOrEmpty();
        var forged = Credentials(transport, next) with
        {
            Subject = "another-subject", IdToken = transport.CreateIdentity(next.Nonce, SubjectValue: "another-subject")
        };
        await store.Invoking(Value => Value.CompleteAsync(nextPairing.Code, forged, default)).Should().ThrowAsync<ChatGptException>();
        (await store.GetCredentialsAsync("owner-a", account.Id, default)).Subject.Should().Be(transport.Subject);
        await store.CompleteAsync(nextPairing.Code, Credentials(transport, next), default);
        (await store.ListAsync("owner-a", default)).Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentRefreshesAcrossInstances_RotateOnce_AndDisconnectStopsRequests()
    {
        using var transport = new ChatGptTestTransport();
        using var http = new HttpClient(transport);
        var oauth = new ChatGptOAuth(http);
        var store = await ResetAsync(oauth);
        var pairing = await store.StartAsync("owner-a", null, default);
        var attempt = await store.PrepareAsync(pairing.Code, default);
        await store.CompleteAsync(pairing.Code, Credentials(transport, attempt) with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30) }, default);
        var account = (await store.ListAsync("owner-a", default)).Single();
        var instances = Enumerable.Range(0, 5).Select(_ => new ChatGptConnectionStore(Database(), oauth)).ToArray();
        var results = await Task.WhenAll(instances.Select(Value => Value.GetCredentialsAsync("owner-a", account.Id, default)));
        transport.Refreshes.Should().Be(1);
        results.Should().OnlyContain(Value => Value.AccessToken == "new-access" && Value.RefreshToken == "rotated-refresh");
        transport.LastGrant!["client_id"].Should().Be("oaiapp_test");
        transport.LastGrant.Should().NotContainKey("scope");
        (await store.DisconnectAsync("owner-a", account.Id, default)).RemoteRevocationConfirmed.Should().BeTrue();
        await store.Invoking(Value => Value.GetCredentialsAsync("owner-a", account.Id, default)).Should().ThrowAsync<ChatGptException>();
        var returning = await store.PrepareAsync((await store.StartAsync("owner-a", account.Id, default)).Code, default);
        returning.ClientId.Should().Be("oaiapp_test");
        returning.IdTokenHint.Should().BeNull();
    }

    [Fact]
    public async Task FailedRemoteRevocation_StillClearsLocalTokens_AndReportsFailure()
    {
        using var transport = new ChatGptTestTransport { RevocationWorks = false };
        using var http = new HttpClient(transport);
        var store = await ResetAsync(new ChatGptOAuth(http));
        var (account, _) = await ConnectAsync(store, transport);
        (await store.DisconnectAsync("owner-a", account.Id, default)).RemoteRevocationConfirmed.Should().BeFalse();
        (await store.ListAsync("owner-a", default)).Single().Connected.Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredPairing_CannotPrepare_AndPolicyDeletionIsNotReseeded()
    {
        using var transport = new ChatGptTestTransport();
        using var http = new HttpClient(transport);
        var store = await ResetAsync(new ChatGptOAuth(http));
        var pairing = await store.StartAsync("owner-a", null, default);
        await ExecuteScalarAsync("UPDATE app.chatgpt_pairings SET expires_at = now() - interval '1 minute'");
        await store.Invoking(Value => Value.PrepareAsync(pairing.Code, default)).Should().ThrowAsync<ChatGptException>();
        (await store.ReadReviewedModelsAsync(default)).Should().Contain("gpt-6.1-sol");
        await ExecuteScalarAsync("DELETE FROM app.configuration_settings WHERE key = 'ChatGpt:ReviewedModels'");
        await store.InitializeAsync(default);
        (await store.ReadReviewedModelsAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task DifferentWorkspaces_WithTheSameEmail_RemainSeparateRegistrations()
    {
        using var transport = new ChatGptTestTransport();
        using var http = new HttpClient(transport);
        var store = await ResetAsync(new ChatGptOAuth(http));
        await ConnectAsync(store, transport);
        var pairing = await store.StartAsync("owner-a", null, default);
        var attempt = await store.PrepareAsync(pairing.Code, default);
        await store.CompleteAsync(pairing.Code, Credentials(transport, attempt) with
        {
            ClientId = "oaiapp_workspace2", IdToken = transport.CreateIdentity(attempt.Nonce, "oaiapp_workspace2")
        }, default);
        var accounts = await store.ListAsync("owner-a", default);
        accounts.Should().HaveCount(2);
        accounts.Select(Value => Value.Label).Distinct().Should().HaveCount(2);
    }

    internal static ChatGptCredentials Credentials(ChatGptTestTransport Transport, ChatGptAuthorization Attempt) =>
        new("oaiapp_test", Transport.Subject, "untrusted@example.test", Transport.CreateIdentity(Attempt.Nonce),
            "initial-access", "initial-refresh", ChatGptOAuth.Scope, DateTimeOffset.UtcNow.AddHours(1));

    private async Task<(ChatGptConnection Account, ChatGptAuthorization Attempt)> ConnectAsync(ChatGptConnectionStore Store, ChatGptTestTransport Transport)
    {
        var pairing = await Store.StartAsync("owner-a", null, default);
        var attempt = await Store.PrepareAsync(pairing.Code, default);
        await Store.CompleteAsync(pairing.Code, Credentials(Transport, attempt), default);
        return ((await Store.ListAsync("owner-a", default)).Single(), attempt);
    }

    private IntegrationDatabase Database() => new(Fixture.ConnectionString, Key);

    [Fact]
    public async Task Catalog_UsesOnlyThisActorsReviewedVisibleModels_AndKeepsGlobalModelsOnProviderFailure()
    {
        using var transport = new ChatGptTestTransport();
        using var http = new HttpClient(transport);
        var store = await ResetAsync(new ChatGptOAuth(http));
        var (account, _) = await ConnectAsync(store, transport);
        var global = new Mock<IChatModelCatalog>();
        global.Setup(Value => Value.GetModelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new("global", "Global", true)]);
        using var inventory = new Inventory();
        using var modelHttp = new HttpClient(inventory);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(Value => Value.CreateClient("ChatGPT")).Returns(modelHttp);
        var catalog = new ChatGptModelCatalog(store, factory.Object, global.Object, NullLogger<ChatGptModelCatalog>.Instance);
        var models = await catalog.GetModelsAsync("owner-a", default);
        models.Select(Value => Value.Id).Should().Equal("global", ChatGptModelIds.Create(account.Id, "gpt-6.1-sol"));
        (await catalog.GetModelsAsync("owner-b", default)).Should().ContainSingle().Which.Id.Should().Be("global");
        inventory.Status = HttpStatusCode.ServiceUnavailable;
        (await catalog.GetModelsAsync("owner-a", default)).Should().ContainSingle().Which.Id.Should().Be("global");
        inventory.Status = HttpStatusCode.OK;
        await ExecuteScalarAsync("UPDATE app.configuration_settings SET value = '[]' WHERE key = 'ChatGpt:ReviewedModels'");
        (await catalog.GetModelsAsync("owner-a", default)).Should().ContainSingle().Which.Id.Should().Be("global");
    }

    private sealed class Inventory : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            Request.RequestUri!.AbsoluteUri.Should().Be(ChatGptOAuth.Resource + "/models");
            Request.Headers.Authorization!.Parameter.Should().Be("initial-access");
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = JsonContent.Create(new { models = new[]
                {
                    new { slug = "gpt-6.1-sol", display_name = "GPT Sol", visibility = "list" },
                    new { slug = "unreviewed", display_name = "Unreviewed", visibility = "list" },
                    new { slug = "gpt-6-astra", display_name = "Hidden", visibility = "hidden" }
                } })
            });
        }
    }
    private async Task<ChatGptConnectionStore> ResetAsync(ChatGptOAuth OAuth)
    {
        await ExecuteScalarAsync("""
            CREATE SCHEMA IF NOT EXISTS app;
            DROP TABLE IF EXISTS app.chatgpt_connections, app.chatgpt_pairings, app.chatgpt_hosts;
            CREATE TABLE IF NOT EXISTS app.configuration_settings(scope text NOT NULL, key text NOT NULL, value text NOT NULL,
                is_secret boolean NOT NULL, is_active boolean NOT NULL DEFAULT true, PRIMARY KEY(scope,key));
            DELETE FROM app.configuration_settings WHERE key = 'ChatGpt:ReviewedModels';
            """);
        var store = new ChatGptConnectionStore(Database(), OAuth);
        await store.InitializeAsync(default);
        return store;
    }

    private async Task<object?> ExecuteScalarAsync(string Sql)
    {
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(Sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
