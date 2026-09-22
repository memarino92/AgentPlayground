using PersonalAgent.Integrations;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class JevChatIntegrationTests
{
    [Fact]
    public async Task DirectNotification_ExecutesAndPersistsWithoutChatOrEmbeddings()
    {
        using var Test = new Harness(JevRoutingMode.DirectTools, ExtraToolName: "publish_mobile_notification");
        Test.DecisionName = "publish_mobile_notification";
        var Answer = await Test.Chat.SendMessageAsync(Test.Id.ToString(), Test.Access, "notify me: drink water");
        Answer.Should().Be("synthetic-tool-result");
        Test.ExtraToolCalls.Should().Be(1);
        Test.LastArgument.Should().Be("drink water");
        Test.ChatFactory.VerifyNoOtherCalls();
        Test.Embeddings.VerifyNoOtherCalls();
        Test.Store.Verify(Store => Store.SaveInteractionAsync(Test.Id, "notify me: drink water", Answer!, Test.State, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("schedule_notification", "Local")]
    [InlineData("tavily_search", "TavilyMcp")]
    public async Task SuggestedTool_IsInvokedByChatWithArguments_AndItsResultIsPersisted(string ToolName, string Source)
    {
        using var Test = new Harness(JevRoutingMode.DirectReadOnly, MemoryEnabled: false, ExtraToolName: ToolName, ExtraSource: Source);
        Test.DecisionName = ToolName;
        var Calls = 0;
        var Client = new Mock<IChatClient>();
        Client.Setup(Value => Value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken _) =>
            {
                Calls++;
                Options!.Tools!.Select(Tool => Tool.Name).Should().BeEquivalentTo("get_current_date_time", ToolName);
                if (Calls == 1)
                {
                    Test.ExtraToolCalls.Should().Be(0);
                    Messages.Should().Contain(Message => Message.Role == ChatRole.System && Message.Text.Contains($"router suggests considering {ToolName}"));
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                        [new FunctionCallContent("synthetic-call", ToolName, new Dictionary<string, object?> { ["payload"] = "validated-by-handler" })])));
                }
                var Result = Messages.SelectMany(Message => Message.Contents).OfType<FunctionResultContent>().Single();
                Result.Result!.ToString().Should().Contain("synthetic-tool-result");
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Completed using the tool result")));
            });
        Test.ChatFactory.Setup(Factory => Factory.Create("test-model")).Returns(Client.Object);
        var Answer = await Test.Chat.SendMessageAsync(Test.Id.ToString(), Test.Access, "Synthetic task request");
        Answer.Should().Be("Completed using the tool result");
        Calls.Should().Be(2);
        Test.ExtraToolCalls.Should().Be(1);
        Test.LastArgument.Should().Be("validated-by-handler");
        Test.Store.Verify(Store => Store.SaveInteractionAsync(Test.Id, "Synthetic task request", Answer!, Test.State, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DirectRoute_SavesInteraction_WithoutChatOrEmbeddings(bool SaveSucceeds)
    {
        using var Test = new Harness(JevRoutingMode.DirectReadOnly, SaveSucceeds);
        var Answer = await Test.Chat.SendMessageAsync(Test.Id.ToString(), Test.Access, "What time is it?");
        Answer.Should().Be(SaveSucceeds ? "Synthetic clock result" : null);
        Test.ToolCalls.Should().Be(1);
        Test.Store.Verify(Store => Store.SaveInteractionAsync(Test.Id, "What time is it?", "Synthetic clock result", Test.State, It.IsAny<CancellationToken>()), Times.Once);
        Test.ChatFactory.VerifyNoOtherCalls();
        Test.Embeddings.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task FallbackAndSuggestion_PreserveFullToolSetAndSaveChatAnswer(int Mode, bool ExpectSuggestion)
    {
        using var Test = new Harness((JevRoutingMode)Mode, MemoryEnabled: false);
        var Client = new Mock<IChatClient>();
        Client.Setup(Client => Client.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken _) =>
            {
                Options!.Tools!.Select(Tool => Tool.Name).Should().BeEquivalentTo("get_current_date_time", "other_tool");
                Messages.Any(Message => Message.Role == ChatRole.System && Message.Text.Contains("advisory router", StringComparison.Ordinal)).Should().Be(ExpectSuggestion);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Normal chat answer")));
            });
        Test.ChatFactory.Setup(Factory => Factory.Create("test-model")).Returns(Client.Object);
        (await Test.Chat.SendMessageAsync(Test.Id.ToString(), Test.Access, "What time is it?")).Should().Be("Normal chat answer");
        Test.ToolCalls.Should().Be(0);
        Test.Store.Verify(Store => Store.SaveInteractionAsync(Test.Id, "What time is it?", "Normal chat answer", Test.State, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WrongSubject_IsRejectedBeforeDecisionProvider()
    {
        using var Test = new Harness(JevRoutingMode.DirectReadOnly);
        (await Test.Chat.SendMessageAsync(Test.Id.ToString(), Test.Access with { SubjectProfileId = "another-athlete" }, "What time is it?")).Should().BeNull();
        Test.DecisionCalls.Should().Be(0);
        Test.ToolCalls.Should().Be(0);
    }

    [Fact]
    public async Task PermissionRevokedDuringDecision_IsRecheckedByBoundWrapper()
    {
        using var Test = new Harness(JevRoutingMode.DirectReadOnly);
        Test.OnDecision = () => Test.Permissions[AgentToolKeys.GetCurrentDateTime] = false;
        await FluentActions.Awaiting(() => Test.Chat.SendMessageAsync(Test.Id.ToString(), Test.Access, "What time is it?"))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        Test.ToolCalls.Should().Be(0);
        Test.ChatFactory.VerifyNoOtherCalls();
        Test.Store.Verify(Store => Store.SaveInteractionAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("{\"mode\":\"Unknown\"}")]
    [InlineData("{\"timeoutMilliseconds\":0}")]
    [InlineData("{\"model\":\"jev-latest\"}")]
    [InlineData("{\"model\":null}")]
    [InlineData("{\"minimumProbability\":1.1}")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("null")]
    public void InvalidSettings_AreRejectedBeforePersistence(string Json)
    {
        var Action = () => JevRoutingRuntime.ValidateEdits(new([new("Api", "Jev:Settings", "1", Json, true)]));
        Action.Should().Throw<IntegrationValidationException>().Which.Errors.Should().ContainKey("Jev:Settings");
    }

    [Fact]
    public void Settings_AllowDisablingAndClearingCredentials_ButNotInvalidTokens()
    {
        JevRoutingRuntime.ValidateEdits(new([new("Api", "Jev:Settings", "1", "{\"mode\":\"Off\"}", true), new("Api", "Jev:ApiKey", "1", "", true)]));
        var Action = () => JevRoutingRuntime.ValidateEdits(new([new("Api", "Jev:ApiKey", "1", "secret\nheader", true)]));
        Action.Should().Throw<IntegrationValidationException>();
        new JevRoutingSnapshot(new(), "private-secret").ToString().Should().NotContain("private-secret");
    }

    private sealed class Harness : IDisposable, IJevRoutingSettings, IToolDecisionClient
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string State { get; } = "{\"ModelId\":\"test-model\"}";
        public AgentAccessContext Access { get; } = new("owner", AgentRoles.Owner, "athlete");
        public Mock<IAgentSessionStore> Store { get; } = new();
        public Mock<IAgentChatClientFactory> ChatFactory { get; } = new(MockBehavior.Strict);
        public Mock<IAgentEmbeddingService> Embeddings { get; } = new(MockBehavior.Strict);
        public Dictionary<string, bool> Permissions { get; } = [];
        public int ToolCalls { get; private set; }
        public int DecisionCalls { get; private set; }
        public string DecisionName { get; set; } = "get_current_date_time";
        public int ExtraToolCalls { get; private set; }
        public string? LastArgument { get; private set; }
        public Action? OnDecision { get; set; }
        public AgentChatService Chat { get; }
        public JevRoutingSnapshot Current { get; }
        private readonly ServiceProvider _services;

        public Harness(JevRoutingMode Mode, bool SaveSucceeds = true, bool MemoryEnabled = true,
            string ExtraToolName = "other_tool", string ExtraSource = "Local")
        {
            Current = new(new() { Mode = Mode, AllowUserContent = true }, "synthetic-key");
            var Registry = new Mock<IAgentToolRegistry>();
            Registry.Setup(Registry => Registry.GetRegistrations()).Returns([
                Registration("get_current_date_time", () => { ToolCalls++; return "Synthetic clock result"; }),
                new AgentToolRegistration(
                    new(ExtraSource == "Local" ? "Local:" + ExtraToolName : AgentToolKeys.Tavily(ExtraToolName),
                        ExtraToolName, ExtraToolName, "Test", "Test description", true, true, true, HasSideEffects: true),
                    ExtraSource, (_, _) => ExtraToolName == "publish_mobile_notification"
                    ? AIFunctionFactory.Create((string title, string body) =>
                    {
                        ExtraToolCalls++;
                        LastArgument = body;
                        return "synthetic-tool-result";
                    }, ExtraToolName)
                    : AIFunctionFactory.Create((string payload) =>
                    {
                        ExtraToolCalls++;
                        LastArgument = payload;
                        return "synthetic-tool-result";
                    }, ExtraToolName))]);
            var AccessStore = new Mock<IToolAccessStore>();
            AccessStore.Setup(Store => Store.GetRolePermissionsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Permissions);
            _services = new ServiceCollection().AddLogging().AddSingleton(Registry.Object).AddSingleton(AccessStore.Object)
                .AddSingleton<ToolAccessService>().AddSingleton<AgentToolBinder>().BuildServiceProvider();
            Store.Setup(Store => Store.GetSessionAsync(Id, It.IsAny<CancellationToken>())).ReturnsAsync(new PersistedAgentSession(Id, Access.SubjectProfileId, State, 0, Access.ActorId));
            Store.Setup(Store => Store.GetSessionMessagesAsync(Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            Store.Setup(Store => Store.SaveInteractionAsync(Id, It.IsAny<string>(), It.IsAny<string>(), State, It.IsAny<CancellationToken>())).ReturnsAsync(SaveSucceeds);
            var Memory = new SemanticMemoryService(Embeddings.Object, Mock.Of<IAgentSemanticMemoryStore>(),
                Options.Create(new AgentMemoryOptions { EnableSemanticMemory = MemoryEnabled }), NullLogger<SemanticMemoryService>.Instance);
            Chat = new(Options.Create(new ApiKeyOptions()), Mock.Of<IChatModelCatalog>(), _services.GetRequiredService<ILoggerFactory>(),
                _services, Store.Object, Memory, _services.GetRequiredService<AgentToolBinder>(), NullLogger<AgentChatService>.Instance,
                ChatFactory.Object, new JevRequestRouter(this, this));
        }

        public Task<ToolChoiceResult> ChooseAsync(ToolChoiceRequest Request, JevRoutingSnapshot Snapshot, CancellationToken Token)
        {
            DecisionCalls++;
            OnDecision?.Invoke();
            return Task.FromResult(new ToolChoiceResult(DecisionName, 1, 1));
        }
        public void Dispose() => _services.Dispose();
        private static AgentToolRegistration Registration(string Name, Func<string> Handler) => new(
            new("Local:" + Name, Name, Name, "Core", "Test tool", true, true, true), "Local", (_, _) => AIFunctionFactory.Create(Handler, Name));
    }
}
