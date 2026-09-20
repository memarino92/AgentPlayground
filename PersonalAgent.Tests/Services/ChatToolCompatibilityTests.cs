using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class ChatToolCompatibilityTests
{
    [Theory]
    [InlineData("gpt-5.6-luna", true)]
    [InlineData("gpt-5.6-terra", true)]
    [InlineData("gpt-5.6-luna", false)]
    [InlineData("gpt-5.6-terra", false)]
    public async Task FunctionTools_UseSupportedEffortWithoutMutatingCallerOptions(string ModelId, bool HasTools)
    {
        var Inner = new Mock<IChatClient>();
        ChatOptions? Captured = null;
        Inner.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken Token) =>
            {
                Captured = Options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            });
        var Original = new ChatOptions { Tools = HasTools ? [AIFunctionFactory.Create(() => "evidence", "search")] : null, Reasoning = new() { Effort = ReasoningEffort.Medium } };
        var Client = OpenAiAgentChatClientFactory.ApplyCompatibility(Inner.Object, ModelId);
        await Client.GetResponseAsync([new(ChatRole.User, "question")], Original);
        Captured!.Reasoning!.Effort.Should().Be(HasTools ? ReasoningEffort.None : ReasoningEffort.Medium);
        Original.Reasoning.Effort.Should().Be(ReasoningEffort.Medium);
    }

    [Fact]
    public void OtherModels_KeepTheirClientAndSettings()
    {
        var Client = Mock.Of<IChatClient>();
        OpenAiAgentChatClientFactory.ApplyCompatibility(Client, "gpt-5.4-mini").Should().BeSameAs(Client);
    }
}
