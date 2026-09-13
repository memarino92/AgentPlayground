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
    public void FailedFirstSend_ReusesEmptyChatInsteadOfCreatingAnother()
    {
        using var Handler = new CatalogHandler { FailMessage = true };
        Configure(Handler);
        var Cut = RenderComponent<Chat>();
        Cut.WaitForElement("textarea").Input("First attempt");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Failed to send message"));
        Cut.FindAll("button").Single(Value => Value.TextContent == "New chat").Click();
        Handler.FailMessage = false;
        Cut.Find("textarea").Input("Retry");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("Synthetic reply"));
        Handler.SelectedModels.Should().ContainSingle();
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
        Cut.FindAll("button").First(Value => Value.TextContent == "New chat").Click();
        Handler.SelectedModels.Should().BeEmpty("blank chats are local drafts");
        Cut.Find("textarea").Input("First message");
        Cut.FindAll("button").Single(Value => Value.TextContent == "Send").Click();
        Cut.WaitForAssertion(() => Handler.SelectedModels.Should().Equal("newly-available-model"));
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
        Cut.Find("select.composer__model").GetAttribute("value").Should().Be("newly-available-model");
        Cut.FindComponent<ChatSessionPanel>().Should().NotBeNull();
        await Cut.InvokeAsync(() => Navigation.NavigateTo(First));
        Cut.WaitForAssertion(() => Cut.Markup.Should().Contain("First saved message"));
        Cut.Find("select.composer__model").GetAttribute("value").Should().Be("provider-default");
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
            if (Request.Method == HttpMethod.Put)
            {
                ChangedModel = (await Request.Content!.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("modelId").GetString();
                return new(HttpStatusCode.NoContent);
            }
            if (Request.Method == HttpMethod.Get && Request.RequestUri.AbsolutePath.EndsWith("/messages"))
            {
                var First = Request.RequestUri.AbsolutePath.Contains(FirstId.ToString());
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HistoryResponse(
                    (First ? FirstId : SecondId).ToString(), First ? "provider-default" : "newly-available-model",
                    [new("user", First ? "First saved message" : "Second saved message")])) };
            }
            if (Request.Method == HttpMethod.Post && Request.RequestUri.AbsolutePath.EndsWith("/messages"))
                return FailMessage ? new(HttpStatusCode.InternalServerError)
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { response = "Synthetic reply" }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { sessions = new[] {
                new SessionListItem(FirstId.ToString(), "First chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new SessionListItem(SecondId.ToString(), "Second chat", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) }, hasMore = false }) };
        }
    }
}
