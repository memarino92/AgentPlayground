using PersonalAgent.Contracts;
using FluentAssertions;
using Xunit;

namespace PersonalAgent.Contracts.Tests;

public sealed class ChatCardTests
{
    [Fact]
    public void GeneratedState_IsResetToProposal_AndUnknownCardsRemainReadable()
    {
        var Parsed = ChatCardParser.Parse("A plan\n```garden-card\n{\"kind\":\"commitment\",\"title\":\"Return package\",\"status\":\"done\",\"revision\":50}\n```");
        Parsed.Text.Should().Be("A plan");
        Parsed.Cards.Single().Status.Should().Be("proposed");
        Parsed.Cards.Single().Revision.Should().Be(0);
        var Invalid = "```garden-card\n{\"kind\":\"execute-js\",\"title\":\"Bad\"}\n```";
        ChatCardParser.Parse(Invalid).Text.Should().Be(Invalid);
        ChatCardParser.Parse(Invalid).Cards.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"kind\":\"checklist\",\"title\":\"Test\",\"items\":null}")]
    [InlineData("{\"kind\":\"checklist\",\"title\":\"Test\",\"items\":[null]}")]
    [InlineData("{\"kind\":\"clarification\",\"title\":\"Test\",\"options\":[null]}")]
    [InlineData("{\"kind\":\"commitment\",\"title\":null}")]
    public void InvalidShape_DoesNotCrashOrCreateAnInteractiveCard(string Json)
    {
        ChatCardParser.Parse($"```garden-card\n{Json}\n```").Cards.Should().BeEmpty();
    }

    [Fact]
    public void Commitments_RequireConfirmation_AndClarificationCannotBeOverwritten()
    {
        var Card = new ChatCard("id", "commitment", "Return package");
        ChatCardParser.Apply(Card, new(0, "complete")).Should().BeNull();
        var Confirmed = ChatCardParser.Apply(Card, new(0, "confirm"));
        ChatCardParser.Apply(Confirmed!, new(0, "complete")).Should().BeNull();
        ChatCardParser.Apply(Confirmed!, new(1, "complete"))!.Status.Should().Be("done");
        var Answer = ChatCardParser.Apply(new("q", "clarification", "Which?"), new(0, "answer", "A"));
        ChatCardParser.Apply(Answer!, new(1, "answer", "B")).Should().BeNull();
    }
}
