using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using MudBlazor.Services;
using PersonalAgent.Web.Components.Pages;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Xunit;

namespace PersonalAgent.Web.Tests.Components;

public sealed class ChatModelDiscoveryTests : TestContext
{
    [Fact]
    public async Task SourceLinkToCurrentConversation_OpensReadonlyHistoryAtTheMessage()
    {
        using var Handler = new CatalogHandler { Conversation = [new("user", "Find this exchange") { Sequence = 3 }] };
        Configure(Handler);
        var Navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Navigation.NavigateTo("/chat?drawer=1");
        var Cut = RenderComponent<Chat>();
        Cut.WaitForAssertion(() => Cut.FindComponent<ChatHistorySearch>().Instance.ProfileId.Should().Be("owner"));
        await Cut.InvokeAsync(() => Navigation.NavigateTo($"/chat?sessionId={Handler.ConversationId}&profileId=owner&message=3"));
        Cut.WaitForAssertion(() => Cut.FindAll("textarea").Should().BeEmpty());
        Cut.Find(".message-list__item--highlight").TextContent.Should().Contain("Find this exchange");
        Navigation.Uri.Should().Contain("message=3");
    }

    [Fact]
    public void ContinuousChat_RestoresByDefault_AndClearUsesServerBoundary()
    {
        using var Handler = new CatalogHandler { Conversation = [new("user", "Saved thought")] };
        Configure(Handler);
        var Cut = RenderComponent<Chat>();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Saved thought"));
        Cut.Markup.Should().NotContain("New chat");
        Cut.Find("textarea").Input("/clear");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Handler.ClearCount.Should().Be(1));
        Cut.Markup.Should().NotContain("Saved thought").And.Contain("Fresh start");
        Handler.OpenCount.Should().Be(1);
    }

    [Fact]
    public void ChatPage_DefaultsToClosedHistoryAndShowsConcisePrompt()
    {
        using var Handler = new CatalogHandler();
        Configure(Handler);
        var Cut = RenderComponent<Chat>();

        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("What's on your mind"));
        Cut.FindAll(".session-panel").Should().BeEmpty();
        Cut.Markup.Should().NotContain("Your digital garden")
            .And.NotContain("A little space to think out loud")
            .And.NotContain("Pick up an idea");
    }

    [Fact]
    public void FailedFirstSend_ReusesEmptyChatInsteadOfCreatingAnother()
    {
        using var Handler = new CatalogHandler { FailMessage = true };
        Configure(Handler);
        var Cut = RenderComponent<Chat>();
        Cut.WaitForElement("textarea").Input("First attempt");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Failed to send message"));
        Cut.Find("textarea").GetAttribute("value").Should().Be("First attempt");
        Handler.FailMessage = false;
        Cut.Find("textarea").Input("Retry");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic reply"));
        Handler.OpenCount.Should().Be(1);
    }

    [Fact]
    public void DeepLinkLoadsWithoutPushingAnotherHistoryEntry()
    {
        using var Handler = new CatalogHandler();
        Configure(Handler);
        var Navigation = (FakeNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Navigation.NavigateTo($"/chat?sessionId={Handler.FirstId}&profileId=owner");
        var Entries = Navigation.History.Count;
        var Cut = RenderComponent<Chat>();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("First saved message"));
        Navigation.History.Count.Should().Be(Entries, "one Back should return to the originating job");
    }

    [Fact]
    public void ChatPage_LoadsPickerFromApi_AndCreatesChatWithSelectedOpaqueId()
    {
        using var Handler = new CatalogHandler();
        Configure(Handler);
        var Cut = RenderComponent<Chat>();
        Cut.WaitForAssertion(() => Handler.CatalogReads.Should().Be(1));
        Cut.WaitForAssertion(() => Cut.FindAll("select.composer__model option").Select(Value => Value.GetAttribute("value"))
            .Should().Equal("provider-default", "newly-available-model"));
        Cut.Find("select.composer__model").Change("newly-available-model");
        Cut.WaitForAssertion(() => Handler.ChangedModel.Should().Be("newly-available-model"));
        Cut.Find("textarea").Input("First message");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic reply"));
        Handler.OpenCount.Should().Be(1);
    }

    [Fact]
    public async Task ChatSelectionUpdatesUrl_KeepsListOpen_AndBackRestoresModel()
    {
        using var Handler = new CatalogHandler();
        Configure(Handler);
        var Navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        Navigation.NavigateTo("/chat?drawer=1");
        var Cut = RenderComponent<Chat>();
        Cut.WaitForElement(".session-panel__entry").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("First saved message"));
        var First = Navigation.Uri;
        First.Should().Contain($"sessionId={Handler.FirstId}").And.Contain("drawer=1");
        Cut.FindAll(".session-panel__entry").Last().Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Second saved message"));
        Cut.FindAll("textarea").Should().BeEmpty("history is read-only");
        Cut.FindComponent<ChatSessionPanel>().Should().NotBeNull();
        await Cut.InvokeAsync(() => Navigation.NavigateTo(First));
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("First saved message"));
        Cut.FindAll("button").Single(Value => Value.TextContent == "Back to conversation").Click();
        Cut.WaitForElement("select.composer__model");
        Cut.Find("select.composer__model").Change("newly-available-model");
        Cut.WaitForAssertion(() => Handler.ChangedModel.Should().Be("newly-available-model"));
        await Cut.InvokeAsync(() => Navigation.NavigateTo("/jobs"));
        Navigation.Uri.Should().EndWith("/jobs", "leaving chat must not rewrite the destination");
    }

    private void Configure(CatalogHandler Handler)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddDataProtection();
        Services.AddScoped<ProtectedLocalStorage>();
        var Auth = new Mock<AuthenticationStateProvider>();
        Auth.Setup(Value => Value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "Owner"), new Claim("urn:github:login", "owner")], "test"))));
        Services.AddCascadingAuthenticationState();
        Services.AddSingleton<AuthenticationStateProvider>(Auth.Object);
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, Auth.Object,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test-signing-key" })));
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public Guid FirstId = Guid.NewGuid(), SecondId = Guid.NewGuid();
        public string? ChangedModel;
        public bool FailMessage;
        public int CatalogReads { get; private set; }
        public int OpenCount { get; private set; }
        public int ClearCount { get; private set; }
        public Guid ConversationId = Guid.NewGuid();
        public List<ConversationMessage> Conversation = [];
        public List<string> SelectedModels { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            if (Request.RequestUri!.AbsolutePath == "/api/models")
            {
                CatalogReads++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { models = new[]
                {
                    new { id = "provider-default", displayName = "Provider default", isDefault = true },
                    new { id = "newly-available-model", displayName = "New model", isDefault = false }
                } }) };
            }
            if (Request.Method == HttpMethod.Post && Request.RequestUri.AbsolutePath == "/api/sessions")
            {
                var Payload = await Request.Content!.ReadFromJsonAsync<JsonElement>(CancellationToken);
                var ModelId = Payload.GetProperty("modelId").GetString()!;
                SelectedModels.Add(ModelId);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { sessionId = Guid.NewGuid().ToString(), modelId = ModelId }) };
            }
            if (Request.RequestUri.AbsolutePath == "/api/conversation/open")
            {
                OpenCount++;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HistoryResponse(ConversationId.ToString(), ChangedModel ?? "provider-default", Conversation)) };
            }
            if (Request.RequestUri.AbsolutePath.EndsWith("/clear"))
            {
                ClearCount++;
                Conversation.Clear();
                return new(HttpStatusCode.NoContent);
            }
            if (Request.Method == HttpMethod.Put)
            {
                ChangedModel = (await Request.Content!.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("modelId").GetString();
                return new(HttpStatusCode.NoContent);
            }
            if (Request.Method == HttpMethod.Get && Request.RequestUri.AbsolutePath.EndsWith("/messages"))
            {
                if (Request.RequestUri.AbsolutePath.Contains(ConversationId.ToString()))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HistoryResponse(ConversationId.ToString(), "provider-default", Conversation)) };
                var First = Request.RequestUri.AbsolutePath.Contains(FirstId.ToString());
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HistoryResponse(
                    (First ? FirstId : SecondId).ToString(), First ? "provider-default" : "newly-available-model",
                    [new("user", First ? "First saved message" : "Second saved message")])) };
            }
            if (Request.Method == HttpMethod.Post && Request.RequestUri.AbsolutePath.EndsWith("/messages"))
            {
                if (FailMessage) return new(HttpStatusCode.InternalServerError);
                var Payload = await Request.Content!.ReadFromJsonAsync<JsonElement>(CancellationToken);
                Conversation.Add(new("user", Payload.GetProperty("message").GetString()!));
                Conversation.Add(new("assistant", "Synthetic reply"));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HistoryResponse(ConversationId.ToString(), ChangedModel ?? "provider-default", Conversation)) };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { sessions = new[] {
                new SessionListItem(FirstId.ToString(), "First chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new SessionListItem(SecondId.ToString(), "Second chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) }, hasMore = false }) };
        }
    }
}
