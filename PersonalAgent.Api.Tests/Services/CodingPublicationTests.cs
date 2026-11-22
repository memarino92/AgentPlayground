using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using PersonalAgent.Api.Coding;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CodingPublicationTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
    [Fact]
    public async Task CodingTranscriptDeliveryIsScopedAndIdempotentAlongsideNormalChat()
    {
        var SessionOptions = Options.Create(new AgentMemoryOptions
        {
            ConnectionString = Database.ConnectionString, Schema = "coding_chat_" + Guid.NewGuid().ToString("N"),
            EnableSemanticMemory = false
        });
        await new AgentMemorySchemaInitializer(SessionOptions, NullLogger<AgentMemorySchemaInitializer>.Instance).StartAsync(default);
        var Sessions = new PostgresAgentSessionStore(SessionOptions, NullLogger<PostgresAgentSessionStore>.Instance);
        var Session = Guid.NewGuid();
        var Event = Guid.NewGuid();
        await Sessions.CreateSessionAsync(Session, "owner", "{}");
        (await Sessions.PostCodingMessageAsync(Session, "other", "owner", Event, "Job completed", default)).Should().BeFalse();
        (await Sessions.HasCodingMessagesAsync(Session, "owner", "owner", 0, default)).Should().BeFalse();
        await using (await Sessions.LockConversationAsync(Session, default))
            (await Sessions.PostCodingMessageAsync(Session, "owner", "owner", Event, "Job completed", default)).Should().BeNull();
        await Task.WhenAll(
            Sessions.PostCodingMessageAsync(Session, "owner", "owner", Event, "Job completed", default),
            Sessions.SaveInteractionAsync(Session, "New message", "Reply", "{}"));
        (await Sessions.PostCodingMessageAsync(Session, "owner", "owner", Event, "Duplicate", default)).Should().BeTrue();
        var Messages = (await Sessions.GetSessionMessagesAsync(Session))!;
        Messages.Select(Message => Message.Sequence).Should().Equal(1, 2, 3);
        var Posted = Messages.Should().ContainSingle(Message => Message.Content == "Job completed").Which;
        (await Sessions.HasCodingMessagesAsync(Session, "owner", "owner", Posted.Sequence - 1, default)).Should().BeTrue();
        (await Sessions.HasCodingMessagesAsync(Session, "owner", "owner", Posted.Sequence, default)).Should().BeFalse();
        (await Sessions.HasCodingMessagesAsync(Session, "other", "owner", 0, default)).Should().BeFalse();
    }

    [Fact]
    public async Task FollowUpUsesTheExistingPrAndRecoversALostFastForwardResponse()
    {
        var Store = new CodingJobStore(new(Database.ConnectionString, Convert.ToBase64String(new byte[32])));
        await Store.InitializeAsync(default);
        using var Rsa = RSA.Create(2048);
        var Settings = new CodingSettings { Enabled = true, GitHubAppId = 12, InstallationId = 34, Checkpoint = "coding-v2", ImageId = "sha256:" + new string('a', 64) };
        var Current = await Store.SettingsAsync(default);
        await Store.SaveSettingsAsync(new(Current.View.Revision, Settings, "replace", "private-router", "replace", Rsa.ExportRSAPrivateKeyPem()), default);
        var Id = Guid.NewGuid();
        var Parent = new CodingJob { Id = Id, Settings = Settings, Branch = $"feat/platform-improvement-{Id:N}",
            PullRequestUrl = $"https://github.com/{Settings.Repository}/pull/1", Status = "PrOpened" };
        using var Handler = new GitHubHandler { RevisionParent = Parent };
        using var Http = new HttpClient(Handler);
        var Publisher = new GitHubCodingPublisher(Http, Store);
        var Target = await Publisher.PrepareRevisionAsync(Settings, Parent, "review", default);
        Target.Evidence.Should().Contain("Please preserve existing settings.").And.Contain("src/Feature.cs");
        (await Publisher.PrepareRevisionAsync(Settings, Parent, "ci", default)).Evidence.Should().Contain("Expected HTTP 200");
        Handler.Head = new string('a', 40);
        var Rebase = await Publisher.PrepareRevisionAsync(Settings, Parent, "rebase", default);
        Rebase.Head.Should().Be(Handler.Head);
        Rebase.RebaseBase.Should().Be(new string('b', 40));
        Handler.Head = Target.Head;
        var Revision = Parent with { Id = Guid.NewGuid(), RootJobId = Id, PullRequestNumber = Target.Number, BaseSha = Target.Head,
            CreatedAt = DateTimeOffset.UtcNow, Artifact = new(Target.Head, [new("src/Feature.cs", "changed")], [new("dotnet test", 0, "Passed")], "Fixes the test.") };
        await FluentActions.Awaiting(() => Publisher.PublishAsync(Revision, default)).Should().ThrowAsync<HttpRequestException>();
        (await Publisher.PublishAsync(Revision, default)).Should().Be(Parent.PullRequestUrl);
        Handler.RevisionUpdates.Should().Be(1);
        Handler.PrCreates.Should().Be(0);
        Handler.BranchCreates.Should().Be(0);
        Handler.Head = new string('f', 40);
        await FluentActions.Awaiting(() => Publisher.PublishAsync(Revision, default)).Should().ThrowAsync<CodingRevisionConflict>();
        Handler.RevisionUpdates.Should().Be(1);
    }

    [Fact]
    public async Task LostCreateResponseReconcilesOnePrWithoutMergingOrRewritingBranch()
    {
        var Store = new CodingJobStore(new(Database.ConnectionString, Convert.ToBase64String(new byte[32])));
        await Store.InitializeAsync(default);
        using var Rsa = RSA.Create(2048);
        var Settings = new CodingSettings { Enabled = true, GitHubAppId = 12, InstallationId = 34, Checkpoint = "coding-v1", ImageId = "sha256:" + new string('a', 64) };
        var Current = await Store.SettingsAsync(default);
        await Store.SaveSettingsAsync(new(Current.View.Revision, Settings, "replace", "private-router", "replace", Rsa.ExportRSAPrivateKeyPem()), default);
        using var Handler = new GitHubHandler(); using var Http = new HttpClient(Handler);
        var Publisher = new GitHubCodingPublisher(Http, Store);
        var Sha = await Publisher.PrepareAsync(Settings, default);
        var Policy = new Mock<IScheduledActorPolicy>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, default)).ReturnsAsync(AgentRoles.Owner);
        var Permissions = new Mock<IToolAccessStore>();
        Permissions.Setup(P => P.GetRolePermissionsAsync(AgentRoles.Owner, default)).ReturnsAsync(new Dictionary<string, bool> { [CodingJobs.Permission] = true });
        var Registry = new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<AIFunction>()));
        var Tools = new ToolAccessService(Permissions.Object, Registry);
        var Service = new CodingJobService(Store, new(Policy.Object, Mock.Of<ICoachAssignmentStore>(), Tools), Tools, Publisher, null!);
        var Access = new AgentAccessContext("owner", AgentRoles.Owner, "owner");
        var RequestKey = Guid.NewGuid();
        var Created = await Service.StartAsync(Access, new("Improve feature", RequestKey.ToString("D")), default);
        var Retried = await Service.StartAsync(Access, new("Improve feature", RequestKey.ToString("B").ToUpperInvariant()), default);
        Retried.Id.Should().Be(Created.Id);
        var Job = (await Store.GetAsync(Created.Id, default))! with {
            Artifact = new(Sha, [new("src/Feature.cs", "public class Feature {}")], [new("dotnet test", 0, "Passed")], "Adds the feature.") };
        await FluentActions.Awaiting(() => Publisher.PublishAsync(Job, default)).Should().ThrowAsync<HttpRequestException>();
        (await Publisher.PublishAsync(Job, default)).Should().Be("https://github.com/owner/repo/pull/1");
        Handler.PrCreates.Should().Be(1);
        Handler.BranchCreates.Should().Be(1);
        Handler.Paths.Should().NotContain(P => P.Contains("/merge", StringComparison.Ordinal) || P.Contains("/deploy", StringComparison.Ordinal));
        Handler.LastBody!.Should().Contain(Job.Id.ToString());
        Handler.Protected = false;
        await FluentActions.Awaiting(() => Publisher.PrepareAsync(Settings, default)).Should().ThrowAsync<InvalidOperationException>();
        Handler.UseRuleset = true;
        (await Publisher.PrepareAsync(Settings, default)).Should().Be(Sha);
        Handler.AppBypass = true;
        await FluentActions.Awaiting(() => Publisher.PrepareAsync(Settings, default)).Should().ThrowAsync<InvalidOperationException>();
        Handler.AppBypass = false; Handler.Approvals = 0;
        await FluentActions.Awaiting(() => Publisher.PrepareAsync(Settings, default)).Should().ThrowAsync<InvalidOperationException>();
        Handler.Approvals = 1; Handler.HideBypass = true;
        await FluentActions.Awaiting(() => Publisher.PrepareAsync(Settings, default)).Should().ThrowAsync<InvalidOperationException>();
        Settings = Settings with { VerifiedRulesetId = 1, VerifiedRulesetUpdatedAt = "2026-09-27T21:00:00.841-04:00" };
        Current = await Store.SettingsAsync(default);
        await Store.SaveSettingsAsync(new(Current.View.Revision, Settings), default);
        (await Publisher.PrepareAsync(Settings, default)).Should().Be(Sha);
        // Publication checks the same live policy; a changed ruleset cannot use old attestation.
        var VerifiedJob = Job with { Settings = Settings };
        (await Publisher.PublishAsync(VerifiedJob, default)).Should().Contain("/pull/1");
        Handler.UpdatedAt = "2026-09-28T01:00:00.842Z";
        await FluentActions.Awaiting(() => Publisher.PublishAsync(VerifiedJob, default)).Should().ThrowAsync<InvalidOperationException>();
        Handler.UpdatedAt = Settings.VerifiedRulesetUpdatedAt;
        Handler.HideBypass = false; Handler.AppBypass = true;
        await FluentActions.Awaiting(() => Publisher.PrepareAsync(Settings, default)).Should().ThrowAsync<InvalidOperationException>();
        Handler.HideBypass = true;
        Settings = Settings with { VerifiedRulesetId = 2 };
        Current = await Store.SettingsAsync(default);
        await Store.SaveSettingsAsync(new(Current.View.Revision, Settings), default);
        await FluentActions.Awaiting(() => Publisher.PrepareAsync(Settings, default)).Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Awaiting(() => Publisher.PublishAsync(VerifiedJob, default)).Should().ThrowAsync<UnauthorizedAccessException>();
        Current = await Store.SettingsAsync(default);
        await FluentActions.Awaiting(() => Store.SaveSettingsAsync(new(Current.View.Revision,
            Settings with { InstallationId = 35 }), default)).Should().ThrowAsync<ArgumentException>();
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public List<string> Paths = [];
        public int PrCreates, BranchCreates;
        public string? LastBody;
        public bool Protected = true;
        public bool UseRuleset, AppBypass;
        public bool HideBypass;
        public string UpdatedAt = "2026-09-28T01:00:00.841Z";
        public int Approvals = 1;
        public CodingJob? RevisionParent;
        public string Head = new('b', 40);
        public int RevisionUpdates;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            var Path = Request.RequestUri!.AbsolutePath; Paths.Add(Path);
            if (Path.EndsWith("access_tokens", StringComparison.Ordinal))
            {
                var Body = await Request.Content!.ReadFromJsonAsync<JsonElement>(Token);
                Body.GetProperty("permissions").GetProperty("administration").GetString().Should().Be("read");
                return Ok(new { token = "installation-token" });
            }
            if (Path.EndsWith("/check-runs", StringComparison.Ordinal))
                return Ok(new { check_runs = new[] { new { id = 3, conclusion = "failure", name = "Test" } } });
            if (Path.EndsWith("/check-runs/3/annotations", StringComparison.Ordinal))
                return Ok(new[] { new { annotation_level = "failure", path = "tests.cs", message = "Expected HTTP 200" } });
            Request.Headers.Authorization!.Parameter.Should().Be("installation-token");
            if (RevisionParent is { } Parent)
            {
                if (Path.EndsWith("/pulls/1", StringComparison.Ordinal))
                    return Ok(new { state = "open", body = $"<!-- coding-job:{Parent.Id:D} -->", html_url = Parent.PullRequestUrl,
                        head = new { @ref = Parent.Branch, sha = Head, repo = new { full_name = Parent.Settings.Repository } }, @base = new { @ref = "main" } });
                if (Path.EndsWith("/pulls/1/reviews", StringComparison.Ordinal))
                    return Ok(new[] { new { state = "CHANGES_REQUESTED", body = "Please preserve existing settings." } });
                if (Path.EndsWith("/pulls/1/comments", StringComparison.Ordinal))
                    return Ok(new[] { new { path = "src/Feature.cs", body = "Keep the default." } });
                if (Request.Method == HttpMethod.Patch && Path.EndsWith($"/git/refs/heads/{Parent.Branch}", StringComparison.Ordinal))
                {
                    var Update = await Request.Content!.ReadFromJsonAsync<JsonElement>(Token);
                    Update.GetProperty("force").GetBoolean().Should().BeFalse();
                    Head = Update.GetProperty("sha").GetString()!;
                    RevisionUpdates++;
                    throw new HttpRequestException("Lost response after branch update");
                }
            }
            if (Path.EndsWith("/protection", StringComparison.Ordinal)) return UseRuleset ? new(HttpStatusCode.NotFound) : Ok(new { enforce_admins = new { enabled = Protected }, required_pull_request_reviews = new { required_approving_review_count = 1 } });
            if (Path.EndsWith("/rules/branches/main", StringComparison.Ordinal)) return Ok(new[] { new { type = "pull_request", parameters = new { required_approving_review_count = Approvals }, ruleset_source_type = "Repository", ruleset_source = "memarino92/AgentPlayground", ruleset_id = 1 } });
            if (Path.EndsWith("/rulesets/1", StringComparison.Ordinal)) return HideBypass
                ? Ok(new { enforcement = "active", updated_at = UpdatedAt })
                : Ok(new { enforcement = "active", updated_at = UpdatedAt, bypass_actors = AppBypass ? new[] { new { actor_type = "Integration", actor_id = 12 } } : [] });
            if (Path.EndsWith("/AgentPlayground", StringComparison.Ordinal)) return Ok(new { @private = false });
            if (Path.EndsWith("/git/ref/heads/main", StringComparison.Ordinal)) return Ok(new { @object = new { sha = new string('b', 40) } });
            if (Path.Contains("/git/commits/", StringComparison.Ordinal)) return Ok(new { tree = new { sha = new string('c', 40) } });
            if (Path.EndsWith("/git/trees", StringComparison.Ordinal)) return Ok(new { sha = new string('d', 40) });
            if (Path.EndsWith("/git/commits", StringComparison.Ordinal)) return Ok(new { sha = new string('e', 40) });
            if (Path.EndsWith("/git/refs", StringComparison.Ordinal)) { BranchCreates++; return Ok(new { }); }
            if (Path.EndsWith("/pulls", StringComparison.Ordinal) && Request.Method == HttpMethod.Get)
                return Ok(PrCreates == 0 ? Array.Empty<object>() : [new { body = LastBody, @base = new { @ref = "main" }, html_url = "https://github.com/owner/repo/pull/1" }]);
            if (Path.EndsWith("/pulls", StringComparison.Ordinal) && Request.Method == HttpMethod.Post)
            {
                var Body = await Request.Content!.ReadFromJsonAsync<JsonElement>(Token);
                Body.GetProperty("draft").GetBoolean().Should().BeTrue();
                LastBody = Body.GetProperty("body").GetString(); PrCreates++;
                throw new HttpRequestException("Lost response after creation");
            }
            throw new InvalidOperationException("Unexpected GitHub operation: " + Path);
        }
        private static HttpResponseMessage Ok(object Value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(Value) };
    }
}
