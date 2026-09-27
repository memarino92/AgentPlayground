using System.Security.Cryptography;

using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

using PersonalAgent.Integrations;

namespace PersonalAgent.AutomationRunner.Tests;

public sealed class CodingJobStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer Database = new PostgreSqlBuilder("postgres:18").Build();
    private CodingJobStore Store = null!;
    public async Task InitializeAsync()
    {
        await Database.StartAsync();
        Store = new(new(Database.GetConnectionString(), Convert.ToBase64String(new byte[32])));
        await Store.InitializeAsync(default);
    }
    public Task DisposeAsync() => Database.DisposeAsync().AsTask();

    [Fact]
    public async Task DuplicateRequestsAndConcurrentClaimsExecuteOnce()
    {
        var Job = NewJob(); var Key = Guid.NewGuid().ToString();
        await Store.CreateAsync(Job, Key, default);
        (await Store.CreateAsync(NewJob(), Key, default)).Id.Should().Be(Job.Id);
        var Claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Store.ClaimAsync(Job.Id, "cap", "railway-secret", "env", default)));
        Claims.Count(C => C).Should().Be(1);
        var Another = NewJob(); await Store.CreateAsync(Another, Guid.NewGuid().ToString(), default);
        (await Store.ClaimAsync(Another.Id, "cap", "secret", "env", default)).Should().BeFalse();
    }

    [Fact]
    public async Task BudgetIsAtomicAndCancellationRevokesCapability()
    {
        var Job = NewJob(); await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        await Store.ClaimAsync(Job.Id, "cap", "secret", "env", default);
        var Requests = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Store.ReserveModelRequestAsync(Job.Id, "cap", 12, default)));
        Requests.Count(R => R).Should().Be(12);
        (await Store.ReserveModelRequestAsync(Job.Id, "wrong", 100, default)).Should().BeFalse();
        await Store.SaveAsync(Job with { Status = "Cancelled" }, "Running", default);
        (await Store.ReserveModelRequestAsync(Job.Id, "cap", 100, default)).Should().BeFalse();
    }

    [Fact]
    public async Task LateSandboxIdentitySurvivesCancellationAndCleanupSurvivesPublication()
    {
        var Job = NewJob(); await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        await Store.ClaimAsync(Job.Id, "cap", "secret", "env", default);
        await Store.SaveAsync(Job with { Status = "Cancelled" }, "Running", default);
        await Store.AttachSandboxAsync(Job.Id, "vm", default);
        var Cancelled = (await Store.GetAsync(Job.Id, default))!;
        Cancelled.Status.Should().Be("Cancelled"); Cancelled.SandboxId.Should().Be("vm");
        (await Store.LeaseAsync(Job.Id, default)).Credential.Should().Be("secret");
        await Store.ClearLeaseAsync(Job.Id, default);
        await Store.SaveAsync(Cancelled with { Error = "updated" }, "Cancelled", default);
        (await Store.GetAsync(Job.Id, default))!.CleanupState.Should().Be("Destroyed");
        (await Store.LeaseAsync(Job.Id, default)).Credential.Should().BeEmpty();
    }

    [Fact]
    public async Task JobListsAreScopedAndDoNotFetchFullArtifacts()
    {
        var Job = NewJob() with { Artifact = new("base", [new("large.txt", "private content")], [], "Summary") };
        await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        (await Store.ListAsync("other", "owner", default)).Should().BeEmpty();
        (await Store.ListAsync("owner", "other", default)).Should().BeEmpty();
        var List = await Store.ListAsync("owner", "owner", default);
        List.Should().ContainSingle(); List[0].Artifact.Should().BeNull();
        (await Store.GetAsync(Job.Id, default))!.Artifact!.Files[0].Content.Should().Be("private content");
    }

    [Fact]
    public async Task FreshSettingsEncryptSecretsAndRejectStaleWrites()
    {
        using var Rsa = RSA.Create(2048); var Pem = Rsa.ExportRSAPrivateKeyPem();
        var Saved = await Store.SaveSettingsAsync(new(0, new(), "replace", "router-secret", "replace", Pem), default);
        Saved.HasOpenRouterKey.Should().BeTrue(); Saved.HasGitHubPrivateKey.Should().BeTrue();
        await using var C = await Store.OpenAsync(default);
        await using var Q = new NpgsqlCommand("SELECT openrouter_key || github_key FROM app.coding_settings", C);
        ((string)(await Q.ExecuteScalarAsync())!).Should().NotContain("router-secret").And.NotContain("BEGIN RSA");
        await FluentActions.Awaiting(() => Store.SaveSettingsAsync(new(0, new()), default)).Should().ThrowAsync<IntegrationConflictException>();
        var Cleared = await Store.SaveSettingsAsync(new(1, new(), "clear", null, "clear"), default);
        Cleared.HasOpenRouterKey.Should().BeFalse();
    }

    private static CodingJob NewJob() => new() { Id = Guid.NewGuid(), ActorId = "owner", Subject = "owner", Instruction = "Improve platform",
        CreatedAt = DateTimeOffset.UtcNow, Deadline = DateTimeOffset.UtcNow.AddMinutes(20) };
}
