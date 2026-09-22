using System.Text.Json;

using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

using PersonalAgent.Services;

namespace PersonalAgent.Tests.Services;

public sealed class AgentSkillsTests
{
    [Fact]
    public async Task CoachAnswerGrounding_IsAdvertisedAndLoadsFullInstructions()
    {
        using var Provider = PersonalAgentSkills.Create(NullLoggerFactory.Instance);
        ChatOptions? Captured = null;
        var Client = new Mock<IChatClient>();
        Client.Setup(Value => Value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<ChatMessage> _, ChatOptions? Options, CancellationToken _) =>
            {
                Captured = Options;
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
            });

        var Agent = Client.Object.AsBuilder()
            .UseFunctionInvocation()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = "SkillTest",
                ChatOptions = new ChatOptions { Instructions = "Use relevant skills." },
                AIContextProviders = [Provider]
            });
        var Session = await Agent.CreateSessionAsync();

        await Agent.RunAsync([new ChatMessage(ChatRole.User, "What did my coach say about yoke breathing?")], Session);

        Captured.Should().NotBeNull();
        Captured!.Instructions.Should().Contain($"<name>{PersonalAgentSkills.CoachAnswerGrounding}</name>")
            .And.Contain("strongman coaching calls");
        var LoadSkill = Captured.Tools.Should().NotBeNull().And.Subject!
            .OfType<AIFunction>()
            .Single(Tool => Tool.Name == AgentSkillsProvider.LoadSkillToolName);
        var Instructions = await LoadSkill.InvokeAsync(new AIFunctionArguments
        {
            ["skillName"] = PersonalAgentSkills.CoachAnswerGrounding
        });
        Instructions.Should().BeOfType<JsonElement>().Which.GetString().Should()
            .Contain("Use `search_coach_checkins`")
            .And.Contain("Copy each supplied `/evidence/...` relative URL verbatim")
            .And.Contain("A failed search does not prove");
    }
}
