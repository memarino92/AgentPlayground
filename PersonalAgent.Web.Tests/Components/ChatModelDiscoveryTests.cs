using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Bunit;
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
    public void ChatPage_LoadsPickerFromApi_AndCreatesChatWithSelectedOpaqueId()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddDataProtection();
        Services.AddScoped<ProtectedSessionStorage>();
        var Auth = new Mock<AuthenticationStateProvider>();
        Auth.Setup(Value => Value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "Owner"), new Claim("urn:github:login", "owner")], "test"))));
        Services.AddSingleton<AuthenticationStateProvider>(Auth.Object);
        using var Handler = new CatalogHandler();
        Services.AddSingleton(new PersonalAgentClient(new HttpClient(Handler) { BaseAddress = new("http://localhost") }, Auth.Object,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "test-signing-key" })));
        var Cut = RenderComponent<Chat>();
        Cut.WaitForAssertion(() => Handler.CatalogReads.Should().Be(1));
        Cut.FindAll("button").First(Value => Value.TextContent == "New chat").Click();
        Cut.WaitForAssertion(() => Cut.FindAll("select.composer__model option").Select(Value => Value.GetAttribute("value"))
            .Should().Equal("provider-default", "newly-available-model"));
        Cut.Find("select.composer__model").Change("newly-available-model");
        Cut.FindAll("button").First(Value => Value.TextContent == "New chat").Click();
        Cut.WaitForAssertion(() => Handler.SelectedModels.Should().Equal("provider-default", "newly-available-model"));
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
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
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { sessions = Array.Empty<object>(), hasMore = false }) };
        }
    }
}
