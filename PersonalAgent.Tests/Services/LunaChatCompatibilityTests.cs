using FluentAssertions;
using Microsoft.Extensions.AI;
using Moq;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class LunaChatCompatibilityTests
{
    [Fact]
    public async Task FunctionTools_UseSupportedEffortWithoutMutatingCallerOptions()
    {
        var Inner = new Mock<IChatClient>();
        ChatOptions? Captured = null;
        Inner.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> Messages, ChatOptions? Options, CancellationToken Token) =>
            {
                Captured = Options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            });
        var Original = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "evidence", "search")], Reasoning = new() { Effort = ReasoningEffort.Medium } };
        var Client = OpenAiAgentChatClientFactory.ApplyCompatibility(Inner.Object, "gpt-5.6-luna");
        await Client.GetResponseAsync([new(ChatRole.User, "question")], Original);
        Captured!.Reasoning!.Effort.Should().Be(ReasoningEffort.None);
        Original.Reasoning.Effort.Should().Be(ReasoningEffort.Medium);
    }

    [Fact]
    public void OtherModels_KeepTheirClientAndSettings()
    {
        var Client = Mock.Of<IChatClient>();
        OpenAiAgentChatClientFactory.ApplyCompatibility(Client, "gpt-5.4-mini").Should().BeSameAs(Client);
    }
}
