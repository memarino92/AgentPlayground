using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using FluentAssertions;
using Xunit;

using PersonalAgent.Api.Coding;
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
        var Id = Guid.NewGuid();
        var Job = new CodingJob { Id = Id, Instruction = "Improve feature", Settings = Settings, BaseSha = Sha,
            Branch = $"feat/platform-improvement-{Id:N}", CreatedAt = DateTimeOffset.UtcNow,
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
    }

    private sealed class GitHubHandler : HttpMessageHandler
    {
        public List<string> Paths = [];
        public int PrCreates, BranchCreates;
        public string? LastBody;
        public bool Protected = true;
        public bool UseRuleset, AppBypass;
        public int Approvals = 1;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            var Path = Request.RequestUri!.AbsolutePath; Paths.Add(Path);
            if (Path.EndsWith("access_tokens", StringComparison.Ordinal)) return Ok(new { token = "installation-token" });
            Request.Headers.Authorization!.Parameter.Should().Be("installation-token");
            if (Path.EndsWith("/protection", StringComparison.Ordinal)) return UseRuleset ? new(HttpStatusCode.NotFound) : Ok(new { enforce_admins = new { enabled = Protected }, required_pull_request_reviews = new { required_approving_review_count = 1 } });
            if (Path.EndsWith("/rules/branches/main", StringComparison.Ordinal)) return Ok(new[] { new { type = "pull_request", parameters = new { required_approving_review_count = Approvals }, ruleset_source_type = "Repository", ruleset_source = "memarino92/AgentPlayground", ruleset_id = 1 } });
            if (Path.EndsWith("/rulesets/1", StringComparison.Ordinal)) return Ok(new { enforcement = "active", bypass_actors = AppBypass ? new[] { new { actor_type = "Integration", actor_id = 12 } } : [] });
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
