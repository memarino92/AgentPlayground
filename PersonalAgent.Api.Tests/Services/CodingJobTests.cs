using System.Text.Json.Nodes;

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

public sealed class CodingJobTests
{
    [Fact]
    public void ModelGatewayRemovesPremiumRoutingAndCapsOutput()
    {
        var Request = JsonNode.Parse("""{"model":"openai/gpt-5.4-mini","messages":[{"role":"user","content":"hello"}],"max_tokens":999999,"provider":{"only":["expensive"]},"plugins":[{"id":"web"}],"service_tier":"priority"}""")!.AsObject();
        var Safe = CodingModelGateway.Sanitize(Request);
        Safe["max_tokens"]!.GetValue<int>().Should().Be(8192);
        Safe["provider"]!["allow_fallbacks"]!.GetValue<bool>().Should().BeFalse();
        Safe.ContainsKey("plugins").Should().BeFalse(); Safe.ContainsKey("service_tier").Should().BeFalse();
        Request["provider"]!["only"]![0]!.GetValue<string>().Should().Be("expensive");
    }

    [Theory]
    [InlineData("{\"model\":\"other\",\"messages\":[{\"content\":\"hi\"}]}")]
    [InlineData("{\"model\":\"openai/gpt-5.4-mini\",\"messages\":[{\"content\":[{\"type\":\"image_url\"}]}]}")]
    [InlineData("{\"model\":\"openai/gpt-5.4-mini\",\"messages\":[{\"content\":\"hi\"}],\"tools\":[{\"type\":\"web_search\"}]}")]
    public void GatewayRejectsUnbudgetedCapabilities(string Input) =>
        FluentActions.Invoking(() => CodingModelGateway.Sanitize(JsonNode.Parse(Input)!.AsObject())).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(".github/workflows/build.yml")]
    [InlineData(".git/config")]
    [InlineData("/absolute")]
    [InlineData("keys/server.pem")]
    [InlineData("src/.env")]
    public void PublisherRejectsUnsafePaths(string Path) =>
        FluentActions.Invoking(() => GitHubCodingPublisher.ValidateArtifact(Job(new(Path, "content")))).Should().Throw<ArgumentException>();

    [Fact]
    public void PublisherRequiresMatchingBasePassingTestsAndRegularFiles()
    {
        var Good = Job(new("src/Feature.cs", "content"));
        GitHubCodingPublisher.ValidateArtifact(Good);
        FluentActions.Invoking(() => GitHubCodingPublisher.ValidateArtifact(Good with { Artifact = Good.Artifact! with { BaseSha = new('b', 40) } })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitHubCodingPublisher.ValidateArtifact(Good with { Artifact = Good.Artifact! with { Checks = [new("test", 1, "failed")] } })).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => GitHubCodingPublisher.ValidateArtifact(Job(new("src/link", "target", "120000")))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GatewayStripsExtraMessageAndTextPartFields()
    {
        var Request = JsonNode.Parse("""{"model":"openai/gpt-5.4-mini","messages":[{"role":"user","audio":{"id":"remote"},"content":[{"type":"text","text":"hello","cache_control":{"type":"ephemeral"}}]}]}""")!.AsObject();
        var Message = CodingModelGateway.Sanitize(Request)["messages"]![0]!.AsObject();
        Message.ContainsKey("audio").Should().BeFalse();
        Message["content"]![0]!.AsObject().ContainsKey("cache_control").Should().BeFalse();
    }

    [Fact]
    public void SettingsRejectBudgetOvercommit() =>
        FluentActions.Invoking(() => CodingJobStore.ValidateSettings(new() { MaxModelRequests = 12, ModelBudgetUsd = 1 })).Should().Throw<ArgumentException>();

    [Fact]
    public async Task CurrentOwnerAndExplicitGrantAreRequired()
    {
        var Policy = new Mock<IScheduledActorPolicy>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, default)).ReturnsAsync(AgentRoles.Owner);
        var Grants = new Dictionary<string, bool>();
        var Permissions = new Mock<IToolAccessStore>();
        Permissions.Setup(P => P.GetRolePermissionsAsync(It.IsAny<string>(), default)).ReturnsAsync(Grants);
        var Registry = new AgentToolRegistry(Mock.Of<ITavilyMcpToolProvider>(P => P.GetTools() == Array.Empty<AIFunction>()));
        var Tools = new ToolAccessService(Permissions.Object, Registry);
        var Subjects = new ScheduledJobAuthorization(Policy.Object, Mock.Of<ICoachAssignmentStore>(), Tools);
        var Service = new CodingJobService(null!, Subjects, Tools, Mock.Of<ICodingPublisher>());
        await FluentActions.Awaiting(() => Service.RequireAsync("owner", null, "owner", default)).Should().ThrowAsync<UnauthorizedAccessException>();
        Grants[CodingJobs.Permission] = true;
        await Service.RequireAsync("owner", null, "owner", default);
        await FluentActions.Awaiting(() => Service.RequireAsync("owner", null, "another-subject", default)).Should().ThrowAsync<UnauthorizedAccessException>();
        Policy.Setup(P => P.ResolveRoleAsync("owner", null, default)).ReturnsAsync((string?)null);
        await FluentActions.Awaiting(() => Service.RequireAsync("owner", null, "owner", default)).Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static CodingJob Job(CodingFile File)
    {
        var Id = Guid.NewGuid(); var Sha = new string('a', 40);
        return new() { Id = Id, BaseSha = Sha, Branch = $"feat/platform-improvement-{Id:N}",
            Artifact = new(Sha, [File], [new("dotnet test", 0, "passed")], "Change summary") };
    }
}
