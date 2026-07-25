using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using MudBlazor.Services;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public class ChatSessionPanelTests : TestContext
{
    public ChatSessionPanelTests() => Services.AddMudServices();

    [Fact]
    public void SessionPanel_ShowsEmptyState_WhenNoSessionsExist()
    {
        var cut = RenderComponent<ChatSessionPanel>(parameters => parameters
            .Add(component => component.Sessions, new List<SessionListItem>())
            .Add(component => component.IsLoadingSessions, false)
            .Add(component => component.IsBusy, false)
            .Add(component => component.HasMoreSessions, false)
            .Add(component => component.SelectedSessionOption, string.Empty)
            .Add(component => component.OnSessionSelectionChanged, EventCallback.Factory.Create<string?>(this, _ => Task.CompletedTask))
            .Add(component => component.OnLoadMoreSessions, EventCallback.Factory.Create(this, () => Task.CompletedTask))
            .Add(component => component.OnCloseDrawer, EventCallback.Factory.Create(this, () => Task.CompletedTask)));

        cut.Markup.Should().Contain("No chats yet.");
    }

    [Fact]
    public void SessionPanel_RendersSessionList_WhenSessionsExist()
    {
        var cut = RenderComponent<ChatSessionPanel>(parameters => parameters
            .Add(component => component.Sessions, new List<SessionListItem> { new("session-1", "First chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) })
            .Add(component => component.IsLoadingSessions, false)
            .Add(component => component.IsBusy, false)
            .Add(component => component.HasMoreSessions, false)
            .Add(component => component.SelectedSessionOption, string.Empty)
            .Add(component => component.OnSessionSelectionChanged, EventCallback.Factory.Create<string?>(this, _ => Task.CompletedTask))
            .Add(component => component.OnLoadMoreSessions, EventCallback.Factory.Create(this, () => Task.CompletedTask))
            .Add(component => component.OnCloseDrawer, EventCallback.Factory.Create(this, () => Task.CompletedTask)));

        cut.Find(".session-panel__list").Should().NotBeNull();
        cut.Find("button.session-panel__entry").TextContent.Should().Contain("First chat");
    }

    [Fact]
    public void SessionPanel_RendersSessionsAndSelectionCallback()
    {
        string? selectedSessionId = null;

        var cut = RenderComponent<ChatSessionPanel>(parameters => parameters
            .Add(component => component.Sessions, new List<SessionListItem> { new("session-1", "First chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) })
            .Add(component => component.IsLoadingSessions, false)
            .Add(component => component.IsBusy, false)
            .Add(component => component.HasMoreSessions, true)
            .Add(component => component.SelectedSessionOption, string.Empty)
            .Add(component => component.OnSessionSelectionChanged, EventCallback.Factory.Create<string?>(this, value => selectedSessionId = value))
            .Add(component => component.OnLoadMoreSessions, EventCallback.Factory.Create(this, () => Task.CompletedTask))
            .Add(component => component.OnCloseDrawer, EventCallback.Factory.Create(this, () => Task.CompletedTask)));

        cut.Find("button.session-panel__entry").Click();
        selectedSessionId.Should().Be("session-1");
        cut.Markup.Should().Contain("Load more");
    }

    [Fact]
    public void SessionPanel_InvokesCloseDrawer_WhenCloseClicked()
    {
        var closed = false;

        var cut = RenderComponent<ChatSessionPanel>(parameters => parameters
            .Add(component => component.Sessions, new List<SessionListItem>())
            .Add(component => component.IsLoadingSessions, false)
            .Add(component => component.IsBusy, false)
            .Add(component => component.HasMoreSessions, false)
            .Add(component => component.SelectedSessionOption, string.Empty)
            .Add(component => component.OnSessionSelectionChanged, EventCallback.Factory.Create<string?>(this, _ => Task.CompletedTask))
            .Add(component => component.OnLoadMoreSessions, EventCallback.Factory.Create(this, () => Task.CompletedTask))
            .Add(component => component.OnCloseDrawer, EventCallback.Factory.Create(this, () => closed = true)));

        cut.FindAll("button").Single(button => button.TextContent == "Close").Click();
        closed.Should().BeTrue();
    }
}
