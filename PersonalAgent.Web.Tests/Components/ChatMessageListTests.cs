using Bunit;
using FluentAssertions;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public class ChatMessageListTests : TestContext
{
    public ChatMessageListTests()
    {
        var Module = JSInterop.SetupModule("./Components/Pages/ChatMessageList.razor.js");
        Module.SetupVoid("initialize", _ => true);
        Module.SetupVoid("dispose", _ => true);
    }

    [Fact]
    public void MessageList_RendersEachMessageWithRoleLabels()
    {
        var cut = RenderComponent<ChatMessageList>(parameters => parameters
            .Add(component => component.Messages, [new ConversationMessage("user", "Hi"), new ConversationMessage("assistant", "Hello")])
            .Add(component => component.IsSendingMessage, false));

        cut.FindAll(".message-list__item").Should().HaveCount(2);
        cut.Find("[data-role='user'] strong").TextContent.Should().Be("You");
        cut.Find("[data-role='assistant'] strong").TextContent.Should().Be("assistant");
        cut.Find(".message-list__item").ClassList.Should().NotContain("border");
    }

    [Fact]
    public void MessageList_ShowsThinkingIndicator_WhenSending()
    {
        var cut = RenderComponent<ChatMessageList>(parameters => parameters
            .Add(component => component.Messages, [])
            .Add(component => component.IsSendingMessage, true));

        cut.Markup.Should().Contain("Thinking...");
        cut.Find("[data-role='assistant']").Should().NotBeNull();
    }
}
