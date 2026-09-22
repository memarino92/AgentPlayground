using PersonalAgent.Contracts;
using Bunit;
using FluentAssertions;
using PersonalAgent.Web.Components.Pages;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class ChatCardsTests : TestContext
{
    [Fact]
    public void Commitment_ConfirmsWithoutInventingAReminder_AndRendersUntrustedLabelsAsText()
    {
        ChatCardAction? Action = null;
        var Cut = RenderComponent<ChatCommitmentCard>(Parameters => Parameters
            .Add(Component => Component.Card, new ChatCard("id", "commitment", "<script>bad()</script>"))
            .Add(Component => Component.OnAction, Value => Action = Value));
        Cut.FindAll("script").Should().BeEmpty();
        Cut.Find("small").TextContent.Should().Contain("does not schedule a reminder");
        Cut.Find("button").Click();
        Action.Should().Be(new ChatCardAction(0, "confirm"));
    }

    [Fact]
    public void Checklist_EmitsRevisionAndItemId_WithoutMutatingInput()
    {
        var Card = new ChatCard("id", "checklist", "Pack") { Revision = 3, Items = [new("receipt", "Receipt")] };
        ChatCardAction? Action = null;
        var Cut = RenderComponent<ChatChecklist>(Parameters => Parameters.Add(Component => Component.Card, Card)
            .Add(Component => Component.OnAction, Value => Action = Value));
        Cut.Find("input").Change(true);
        Action.Should().Be(new ChatCardAction(3, "toggle", "receipt"));
        Card.Items.Single().Done.Should().BeFalse();
    }

    [Fact]
    public void Clarification_AllowsFreeText_AndDisablesActionsInReadonlyHistory()
    {
        ChatCardAction? Action = null;
        var Cut = RenderComponent<ChatClarificationForm>(Parameters => Parameters
            .Add(Component => Component.Card, new ChatCard("id", "clarification", "Which?") { Options = ["Garden", "Deck"] })
            .Add(Component => Component.OnAction, Value => Action = Value));
        Cut.Find("input").Input("Something else");
        Cut.FindAll("button").Last().Click();
        Action.Should().Be(new ChatCardAction(0, "answer", "Something else"));
        Cut.SetParametersAndRender(Parameters => Parameters.Add(Component => Component.Disabled, true));
        Cut.FindAll("button").Should().OnlyContain(Button => Button.HasAttribute("disabled"));
    }
}
