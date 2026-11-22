using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;

namespace PersonalAgent.Api.Tests.Services;

public sealed class ResearchSwarmTests
{
    private static readonly AgentAccessContext Owner = new("owner", AgentRoles.Owner, "profile") { ChatModelId = "selected-model" };
    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new { type = "object" });

    [Fact]
    public async Task AuthorizedResearchBranchesSearchConcurrentlyAndPreserveEvidenceWhenOneFails()
    {
        var AllStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var Calls = 0;
        var Search = new TestSearch(async (Arguments, Token) =>
        {
            if (Interlocked.Increment(ref Calls) == 3) AllStarted.SetResult();
            await AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Arguments["max_results"].Should().Be(3);
            Arguments["search_depth"].Should().Be("basic");
            Arguments["include_raw_content"].Should().Be(false);
            return (string)Arguments["query"]! switch
            {
                "First angle" => new { structuredContent = new { results = new[] {
                    new { url = "https://example.org/study", title = "Study", content = "Source snippet" },
                    new { url = "javascript:throw 1", title = "Invalid", content = "Rejected snippet" },
                    new { url = "http://localhost/admin", title = "Local", content = "Rejected domain" },
                    new { url = "https://example.org/study", title = "Duplicate", content = "Repeated" }
                } } },
                "Second angle" => new { content = new[] { new { type = "text", text = """
                    Request ID: ignore-this
                    Detailed Results:

                    Title: Official report
                    URL: https://example.gov/report
                    Content: Report snippet
                    Raw Content: Private or excessive fields do not reach the research worker.
                    """ } } },
                _ => new { isError = true, content = new[] { new { type = "text", text = "Provider internals" } } }
            };
        });
        var Client = new Mock<IChatClient>();
        Client.Setup(Model => Model.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<ChatMessage> Messages, ChatOptions Options, CancellationToken _) =>
            {
                Options.Tools.Should().BeNullOrEmpty();
                var Context = Messages.Single(Message => Message.Role == ChatRole.User).Text;
                Context.Should().NotContain("Provider internals").And.NotContain("Private or excessive")
                    .And.NotContain("ignore-this").And.NotContain("Rejected snippet");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Finding [1]"));
            });
        var Factory = new Mock<IAgentChatClientFactory>();
        Factory.Setup(Value => Value.CreateForActor("selected-model", "owner")).Returns(Client.Object);
        var Tool = Build(Search, Factory.Object);

        var Result = await Tool.RunAsync(Owner, ["First angle", "Second angle", "Third angle"], default);

        Result.Status.Should().Be("Partial");
        Result.Branches.Select(Branch => Branch.Status).Should().Equal("Completed", "Completed", "SearchFailed");
        Result.Branches[0].Sources.Should().ContainSingle().Which.Should().Be(new ResearchSource("https://example.org/study", "Study", "Source snippet"));
        Result.Branches[1].Sources.Should().ContainSingle().Which.Url.Should().Be("https://example.gov/report");
        Result.Branches[1].Findings.Should().Be("Finding [1]");
        Factory.Verify(Value => Value.CreateForActor("selected-model", "owner"), Times.Exactly(2));
    }

    [Fact]
    public async Task SummaryProviderErrorsRetainBoundedOriginalSources()
    {
        var Search = new TestSearch((_, _) => ValueTask.FromResult<object?>(new { results = new[] {
            new { url = "https://example.org/study", title = "Study", content = new string('x', 2400) }
        } }));
        var Factory = new Mock<IAgentChatClientFactory>();
        Factory.Setup(Value => Value.CreateForActor("selected-model", "owner")).Throws(new InvalidOperationException("Private provider error"));
        var Tool = Build(Search, Factory.Object);

        var Result = await Tool.RunAsync(Owner, ["First angle", "Second angle"], default);

        Result.Status.Should().Be("Partial");
        Result.Branches.Should().OnlyContain(Branch => Branch.Status == "SummaryUnavailable" && Branch.Findings == null);
        Result.Branches.Should().OnlyContain(Branch => Branch.Sources[0].Snippet.Length == 600);
    }

    [Fact]
    public async Task PublicSearchRequiresBothExplicitGrantsAndRejectsScheduledAndCoachAccess()
    {
        var Search = new TestSearch((_, _) => throw new InvalidOperationException("Search must not run."));
        var Factory = Mock.Of<IAgentChatClientFactory>();
        var Questions = new[] { "First angle", "Second angle" };

        var MissingSwarmGrant = await Build(Search, Factory, Swarm: false).RunAsync(Owner, Questions, default);
        var MissingSearchGrant = await Build(Search, Factory, Web: false).RunAsync(Owner, Questions, default);
        var Coach = await Build(Search, Factory).RunAsync(Owner with { Role = AgentRoles.Coach }, Questions, default);
        var Scheduled = await Build(Search, Factory).RunAsync(Owner with { ScheduledTaskId = Guid.NewGuid() }, Questions, default);
        var Invalid = await Build(Search, Factory).RunAsync(Owner, ["public topic", " PUBLIC TOPIC "], default);

        foreach (var Result in new[] { MissingSwarmGrant, MissingSearchGrant, Coach, Scheduled })
            Result.Status.Should().Be("Unavailable");
        Invalid.Status.Should().Be("InvalidQuestions");
    }

    private static ResearchSwarm Build(TestSearch Search, IAgentChatClientFactory Factory, bool Swarm = true, bool Web = true)
    {
        var Tavily = new Mock<ITavilyMcpToolProvider>();
        Tavily.SetupGet(Provider => Provider.IsAvailable).Returns(true);
        Tavily.Setup(Provider => Provider.GetTools()).Returns([Search]);
        var Access = new Mock<IToolAccessStore>();
        Access.Setup(Store => Store.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, bool> { [AgentToolKeys.ResearchSwarm] = Swarm, [AgentToolKeys.Tavily(Search.Name)] = Web });
        return new(Tavily.Object, new ToolAccessService(Access.Object, new AgentToolRegistry(Tavily.Object)), Factory,
            NullLogger<ResearchSwarm>.Instance);
    }

    private sealed class TestSearch(Func<AIFunctionArguments, CancellationToken, ValueTask<object?>> Handler) : AIFunction
    {
        public override string Name => "tavily_search";
        public override JsonElement JsonSchema => Schema;
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments Arguments, CancellationToken CancellationToken)
            => Handler(Arguments, CancellationToken);
    }
}
