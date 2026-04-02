using FluentAssertions;
using PersonalAgent.Web.Services;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace PersonalAgent.Web.Tests.Services;

public class PersonalAgentClientTests
{
    [Fact]
    public async Task GetModelsAsync_ThrowsTypedException_WithStatusAndBody()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"missing\"}", Encoding.UTF8, "application/json"),
                ReasonPhrase = "Not Found"
            }));

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new PersonalAgentClient(httpClient);

        var action = async () => await client.GetModelsAsync();

        var ex = await action.Should().ThrowAsync<PersonalAgentApiException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Which.Operation.Should().Be("load models");
        ex.Which.ResponseBody.Should().Contain("missing");
    }

    [Fact]
    public async Task GetModelsAsync_ReturnsPayload_WhenSuccessful()
    {
        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"models\":[{\"id\":\"gpt-4o-mini\",\"displayName\":\"GPT-4o mini\",\"isDefault\":true}]}", Encoding.UTF8, "application/json")
            }));

        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new PersonalAgentClient(httpClient);

        var result = await client.GetModelsAsync();

        result.Should().NotBeNull();
        result!.Models.Should().ContainSingle();
        result.Models[0].Id.Should().Be("gpt-4o-mini");
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
