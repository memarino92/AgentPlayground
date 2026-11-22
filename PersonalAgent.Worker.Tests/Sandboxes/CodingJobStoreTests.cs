using System.Security.Cryptography;

using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

using PersonalAgent.Worker.Sandboxes;

namespace PersonalAgent.Worker.Tests.Sandboxes;

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
        await Store.CancelAsync(Job.Id, default);
        (await Store.ReserveModelRequestAsync(Job.Id, "cap", 100, default)).Should().BeFalse();
    }

    [Fact]
    public async Task LateSandboxIdentitySurvivesCancellationAndCleanupSurvivesPublication()
    {
        var Job = NewJob(); await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        await Store.ClaimAsync(Job.Id, "cap", "secret", "env", default);
        await Store.CancelAsync(Job.Id, default);
        await Store.AttachSandboxAsync(Job.Id, "vm", default);
        var Cancelled = (await Store.GetAsync(Job.Id, default))!;
        Cancelled.Status.Should().Be("Cancelled"); Cancelled.SandboxId.Should().Be("vm");
        (await Store.LeaseAsync(Job.Id, default)).Credential.Should().Be("secret");
        await Store.ClearLeaseAsync(Job.Id, "vm", default);
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
    public async Task CodingQuestionUsesTheOriginatingConversationAndStopsAcceptingAnswersAfterCancellation()
    {
        var Job = NewJob() with { SessionId = Guid.NewGuid() };
        await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        await Store.ClaimAsync(Job.Id, "cap", "secret", "env", default);
        var Request = new AskCodingQuestion(Guid.NewGuid(), "Which database should this use?");
        (await Store.AskAsync(Job.Id, "wrong", Request, default)).Should().BeNull();
        var Question = await Store.AskAsync(Job.Id, "cap", Request, default);
        Question!.SessionId.Should().Be(Job.SessionId!.Value);
        (await Store.AskAsync(Job.Id, "cap", Request with { Id = Guid.NewGuid() }, default)).Should().BeNull();
        (await Store.QuestionsAsync("owner", "owner", Guid.NewGuid(), false, default)).Should().BeEmpty();
        (await Store.AnswerAsync(Request.Id, "other", "owner", null, "SQLite", default)).Should().BeFalse();
        (await Store.AnswerAsync(Request.Id, "owner", "owner", Guid.NewGuid(), "SQLite", default)).Should().BeFalse();
        (await Store.AnswerAsync(Request.Id, "owner", "owner", Job.SessionId, "PostgreSQL", default)).Should().BeTrue();
        (await Store.AnswerAsync(Request.Id, "owner", "owner", Job.SessionId, "SQLite", default)).Should().BeFalse();
        (await Store.AskAsync(Job.Id, "cap", Request, default))!.Answer.Should().Be("PostgreSQL");
        (await Store.QuestionsAsync("owner", "owner", Job.SessionId, false, default)).Should().BeEmpty();
        var Second = Request with { Id = Guid.NewGuid(), Text = "What version?" };
        (await Store.AskAsync(Job.Id, "cap", Second, default)).Should().NotBeNull();
        await Store.CancelAsync(Job.Id, default);
        (await Store.AnswerAsync(Second.Id, "owner", "owner", Job.SessionId, "18", default)).Should().BeFalse();
        (await Store.QuestionsAsync("owner", "owner", Job.SessionId, false, default)).Should().BeEmpty();
        (await Store.UnpostedResultsAsync(default)).Should().ContainSingle().Which.Id.Should().Be(Job.Id);
        await Store.MarkConversationPostAsync(Job.Id, false, default);
        (await Store.UnpostedResultsAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task VerifiedNoChangeJobPostsAResultAndOnlyRemainsPendingForCleanup()
    {
        var Job = NewJob() with { SessionId = Guid.NewGuid(), AllowNoChanges = true };
        await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        await Store.ClaimAsync(Job.Id, "cap", "secret", "env", default);
        await Store.AttachSandboxAsync(Job.Id, "vm", default);
        var Running = (await Store.GetAsync(Job.Id, default))!;
        (await Store.SaveAsync(Running with { Status = CodingJobs.CompletedNoChanges }, "Running", default)).Should().BeTrue();
        (await Store.UnpostedResultsAsync(default)).Should().ContainSingle().Which.Id.Should().Be(Job.Id);
        CodingJobs.Terminal((await Store.GetAsync(Job.Id, default))!.Status).Should().BeTrue();
        (await Store.PendingAsync(default)).Should().ContainSingle().Which.Id.Should().Be(Job.Id);
        await Store.ClearLeaseAsync(Job.Id, "vm", default);
        (await Store.PendingAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task CheckpointWaitsForCleanupAndTheOwnerAndPreservesTheSharedBudget()
    {
        var Job = NewJob() with { AttemptLimit = 2, SessionId = Guid.NewGuid(), BaseSha = new string('a', 40) };
        await Store.CreateAsync(Job, Guid.NewGuid().ToString(), default);
        (await Store.ClaimAsync(Job.Id, "first", "secret", "env", default)).Should().BeTrue();
        await Store.AttachSandboxAsync(Job.Id, "first-vm", default);
        (await Store.ReserveModelRequestAsync(Job.Id, "first", 2, default)).Should().BeTrue();
        var Question = new AskCodingQuestion(Guid.NewGuid(), "Which profile?");
        (await Store.AskAsync(Job.Id, "first", Question, default)).Should().NotBeNull();
        var First = (await Store.GetAsync(Job.Id, default))!;
        var Artifact = new CodingArtifact(Job.BaseSha, [new("src/example.cs", "partial")], [], "Work in progress");
        (await Store.SaveAsync(First with { Status = CodingJobs.Resuming, ResumePhase = "code", Artifact = Artifact }, "Running", default)).Should().BeTrue();
        (await Store.ClaimAsync(Job.Id, "too-early", "secret", "env", default)).Should().BeFalse();
        (await Store.AuthorizeModelRequestAsync(Job.Id, "first", default)).Should().BeFalse();
        await Store.ClearLeaseAsync(Job.Id, "first-vm", default);
        (await Store.QuestionsAsync("owner", "owner", Job.SessionId, false, default)).Should().ContainSingle();
        (await Store.ClaimAsync(Job.Id, "without-answer", "secret", "env", default)).Should().BeFalse();
        (await Store.AnswerAsync(Question.Id, "owner", "owner", Job.SessionId, "Existing owner", default)).Should().BeTrue();
        (await Store.ClaimAsync(Job.Id, "second", "secret", "env", default)).Should().BeTrue();
        await Store.AttachSandboxAsync(Job.Id, "second-vm", default);
        await Store.ClearLeaseAsync(Job.Id, "first-vm", default);
        (await Store.SaveAsync(First with { Status = "Failed" }, "Running", default)).Should().BeFalse();
        var Second = (await Store.GetAsync(Job.Id, default))!;
        Second.Attempt.Should().Be(2);
        Second.SandboxId.Should().Be("second-vm");
        Second.CleanupState.Should().Be("Pending");
        Second.Artifact.Should().BeEquivalentTo(Artifact);
        Second.ModelRequests.Should().Be(1);
        (await Store.ReadClarificationsAsync(Job.Id, default)).Should().ContainSingle().Which.Answer.Should().Be("Existing owner");
        (await Store.ReserveModelRequestAsync(Job.Id, "second", 2, default)).Should().BeTrue();
        (await Store.ReserveModelRequestAsync(Job.Id, "second", 2, default)).Should().BeFalse();
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
