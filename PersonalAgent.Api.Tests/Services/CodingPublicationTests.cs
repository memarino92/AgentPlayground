using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

using PersonalAgent.Api.Coding;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using PersonalAgent.Contracts.Coding;
using PersonalAgent.Integrations;

namespace PersonalAgent.Api.Tests.Services;

public sealed class CodingPublicationTests(PostgresVectorFixture Database) : IClassFixture<PostgresVectorFixture>
{
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
        var Service = new CodingJobService(Store, new(Policy.Object, Mock.Of<ICoachAssignmentStore>(), Tools), Tools, Publisher);
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
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            var Path = Request.RequestUri!.AbsolutePath; Paths.Add(Path);
            if (Path.EndsWith("access_tokens", StringComparison.Ordinal))
            {
                var Body = await Request.Content!.ReadFromJsonAsync<JsonElement>(Token);
                Body.GetProperty("permissions").GetProperty("administration").GetString().Should().Be("read");
                return Ok(new { token = "installation-token" });
            }
            Request.Headers.Authorization!.Parameter.Should().Be("installation-token");
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
