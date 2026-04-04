using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public class ChatComposerTests : TestContext
{
    [Fact]
    public void Composer_RendersMessageInputModelDropdownAndSendButton()
    {
        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.CurrentMessage, string.Empty)
            .Add(component => component.SelectedModelId, "gpt-4o-mini")
            .Add(component => component.Models, new List<AvailableChatModelResponse> { new("gpt-4o-mini", "GPT-4o mini", true) })
            .Add(component => component.IsBusy, false)
            .Add(component => component.CurrentMessageChanged, EventCallback.Factory.Create<string>(this, _ => Task.CompletedTask))
            .Add(component => component.SelectedModelIdChanged, EventCallback.Factory.Create<string>(this, _ => Task.CompletedTask))
            .Add(component => component.OnKeyDown, EventCallback.Factory.Create<KeyboardEventArgs>(this, _ => Task.CompletedTask))
            .Add(component => component.OnSendMessage, EventCallback.Factory.Create(this, () => Task.CompletedTask)));

        cut.Find("input.composer__input").Should().NotBeNull();
        cut.Find("input.composer__input").GetAttribute("value").Should().BeEmpty();
        cut.Find("select.composer__model").Should().NotBeNull();
        cut.Find("select.composer__model").GetAttribute("value").Should().Be("gpt-4o-mini");
        cut.Find("button").TextContent.Should().Be("Send");
    }

    [Fact]
    public void Composer_InvokesCallbacks_WhenUserTypesAndSubmits()
    {
        var message = string.Empty;
        var modelId = "gpt-4o-mini";
        var sendCount = 0;

        var cut = RenderComponent<ChatComposer>(parameters => parameters
            .Add(component => component.CurrentMessage, message)
            .Add(component => component.SelectedModelId, modelId)
            .Add(component => component.Models, new List<AvailableChatModelResponse>
            {
                new("gpt-4o-mini", "GPT-4o mini", true),
                new("gpt-4o", "GPT-4o", false)
            })
            .Add(component => component.IsBusy, false)
            .Add(component => component.CurrentMessageChanged, EventCallback.Factory.Create<string>(this, value => message = value))
            .Add(component => component.SelectedModelIdChanged, EventCallback.Factory.Create<string>(this, value => modelId = value))
            .Add(component => component.OnKeyDown, EventCallback.Factory.Create<KeyboardEventArgs>(this, _ => Task.CompletedTask))
            .Add(component => component.OnSendMessage, EventCallback.Factory.Create(this, () => sendCount++)));

        cut.Find("input").Input("hello");
        message.Should().Be("hello");

        cut.Find("select").Change("gpt-4o");
        modelId.Should().Be("gpt-4o");

        cut.Find("button").Click();
        sendCount.Should().Be(1);

        cut.SetParametersAndRender(parameters => parameters.Add(component => component.CurrentMessage, string.Empty));
        cut.Find("input").GetAttribute("value").Should().BeEmpty();
    }
}
