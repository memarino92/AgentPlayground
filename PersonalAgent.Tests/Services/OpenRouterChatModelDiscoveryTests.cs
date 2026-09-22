using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAI;
using OpenAI.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class OpenRouterChatModelDiscoveryTests
{
    [Fact]
    public async Task Discovery_QualifiesProviderIds_ForStableCatalogSelection()
    {
        using var handler = new ModelInventoryHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAIModelClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = OpenRouterClientProvider.Endpoint,
            Transport = new HttpClientPipelineTransport(http)
        });

        var models = await new OpenRouterChatModelDiscovery(client).GetModelIdsAsync();

        models.Should().Equal("openrouter:openrouter/auto", "openrouter:anthropic/claude-sonnet-4.5");
        handler.Called.Should().BeTrue();
    }

    [Fact]
    public async Task CompositeDiscovery_RetainsHealthyProvider_WhenAnotherFails()
    {
        var healthy = new Mock<IChatModelDiscovery>();
        healthy.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["gpt-test"]);
        var failed = new Mock<IChatModelDiscovery>();
        failed.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var discovery = new CompositeChatModelDiscovery(
        [
            new("OpenAI", () => true, healthy.Object),
            new("OpenRouter", () => true, failed.Object),
            new("Disabled", () => false, Mock.Of<IChatModelDiscovery>(MockBehavior.Strict))
        ], NullLogger<CompositeChatModelDiscovery>.Instance);

        (await discovery.GetModelIdsAsync()).Should().Equal("gpt-test");
    }

    [Fact]
    public async Task CompositeDiscovery_Fails_WhenEveryConfiguredProviderFails()
    {
        var failed = new Mock<IChatModelDiscovery>();
        failed.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        var discovery = new CompositeChatModelDiscovery(
            [new("OpenRouter", () => true, failed.Object)], NullLogger<CompositeChatModelDiscovery>.Instance);

        await discovery.Invoking(Value => Value.GetModelIdsAsync()).Should().ThrowAsync<AggregateException>();
    }

    [Theory]
    [InlineData("openrouter:openrouter/auto", "openrouter/auto")]
    [InlineData("OPENROUTER:google/gemini-test", "google/gemini-test")]
    public void CatalogIds_RoundTrip(string CatalogId, string ProviderId)
    {
        OpenRouterModelIds.TryGetProviderId(CatalogId, out var parsed).Should().BeTrue();
        parsed.Should().Be(ProviderId);
        OpenRouterModelIds.ToCatalogId(ProviderId).Should().Be("openrouter:" + ProviderId);
    }

    private sealed class ModelInventoryHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            Called = true;
            Request.Method.Should().Be(HttpMethod.Get);
            Request.RequestUri!.AbsoluteUri.Should().Be("https://openrouter.ai/api/v1/models");
            Request.Headers.Authorization!.Parameter.Should().Be("test-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"object":"list","data":[
                      {"id":"openrouter/auto","object":"model","created":0,"owned_by":"openrouter"},
                      {"id":"anthropic/claude-sonnet-4.5","object":"model","created":0,"owned_by":"anthropic"}
                    ]}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
