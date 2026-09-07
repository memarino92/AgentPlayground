using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;

using FluentAssertions;
using OpenAI;
using OpenAI.Models;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class OpenAiChatModelDiscoveryTests
{
    [Fact]
    public async Task Discovery_UsesModelsEndpoint_AndReturnsOnlyNeutralIds()
    {
        using var handler = new ModelInventoryHandler();
        using var http = new HttpClient(handler);
        var client = new OpenAIModelClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(http)
        });
        var discovery = new OpenAiChatModelDiscovery(client);

        var models = await discovery.GetModelIdsAsync();

        models.Should().Equal("chat-model", "embedding-model");
        handler.Called.Should().BeTrue();
    }

    private sealed class ModelInventoryHandler : HttpMessageHandler
    {
        public bool Called { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            Called = true;
            Request.Method.Should().Be(HttpMethod.Get);
            Request.RequestUri!.AbsolutePath.Should().Be("/v1/models");
            Request.Headers.Authorization!.Scheme.Should().Be("Bearer");
            Request.Headers.Authorization.Parameter.Should().Be("test-key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"object":"list","data":[
                      {"id":"chat-model","object":"model","created":0,"owned_by":"test-provider"},
                      {"id":"embedding-model","object":"model","created":0,"owned_by":"test-provider"}
                    ]}
                    """, Encoding.UTF8, "application/json")
            });
        }
    }
}
