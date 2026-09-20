using AgentPlayground.Integrations;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class JevChatIntegrationTests
{
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
        public Action? OnDecision { get; set; }
        public AgentChatService Chat { get; }
        public JevRoutingSnapshot Current { get; }
        private readonly ServiceProvider _services;

        public Harness(JevRoutingMode Mode, bool SaveSucceeds = true, bool MemoryEnabled = true)
        {
            Current = new(new() { Mode = Mode, AllowUserContent = true }, "synthetic-key");
            var Registry = new Mock<IAgentToolRegistry>();
            Registry.Setup(Registry => Registry.GetRegistrations()).Returns([
                Registration("get_current_date_time", () => { ToolCalls++; return "Synthetic clock result"; }),
                Registration("other_tool", () => "other")]);
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
            return Task.FromResult(new ToolChoiceResult("get_current_date_time", 1, 1));
        }
        public void Dispose() => _services.Dispose();
        private static AgentToolRegistration Registration(string Name, Func<string> Handler) => new(
            new("Local:" + Name, Name, Name, "Core", "Test tool", true, true, true), "Local", (_, _) => AIFunctionFactory.Create(Handler, Name));
    }
}
