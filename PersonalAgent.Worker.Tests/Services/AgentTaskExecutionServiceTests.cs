using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

using AgentPlayground.Contracts.Messaging.Commands;
using PersonalAgent.Worker.Configuration;
using PersonalAgent.Worker.Services;

namespace PersonalAgent.Worker.Tests.Services;

public class AgentTaskExecutionServiceTests
{
    [Theory]
    [InlineData("catalog-default-alpha")]
    [InlineData("catalog-default-beta")]
    public async Task ExecuteAsync_UsesApiDefaultWithoutSelectingAProviderModel(string DefaultModel)
    {
        using var Handler = new CatalogDefaultHandler(DefaultModel);
        using var Client = new HttpClient(Handler) { BaseAddress = new Uri("http://api.example.test") };
        var Factory = new Mock<IHttpClientFactory>();
        Factory.Setup(Value => Value.CreateClient("PersonalAgentApi")).Returns(Client);
        var Service = new AgentTaskExecutionService(
            Factory.Object,
            Options.Create(new PersonalAgentApiOptions { ActorSigningKey = "synthetic-test-signing-key" }),
            NullLogger<AgentTaskExecutionService>.Instance);

        var Result = await Service.ExecuteAsync(new ExecuteAgentTask
        {
            TaskId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            TenantId = "default",
            UserId = "synthetic-owner",
            ExecuteAtUtc = DateTimeOffset.UtcNow,
            Instruction = "Summarize my garden notes"
        });

        Result.Succeeded.Should().BeTrue();
        Result.Summary.Should().Be($"Completed with {DefaultModel}");
        Handler.RequestCount.Should().Be(2);
    }

    private sealed class CatalogDefaultHandler(string DefaultModel) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            RequestCount++;
            var Body = await Request.Content!.ReadFromJsonAsync<JsonElement>(CancellationToken);
            Request.Headers.Contains("X-Agent-Signature").Should().BeTrue();
            Body.GetProperty("profileId").GetString().Should().Be("synthetic-owner");

            if (Request.RequestUri!.AbsolutePath == "/api/sessions")
            {
                // The API can change its default without any Worker configuration or vendor knowledge.
                var SelectsModel = Body.TryGetProperty("modelId", out var Model) && Model.ValueKind != JsonValueKind.Null;
                if (SelectsModel) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { sessionId = "scheduled-session", modelId = DefaultModel, message = "Created" })
                };
            }

            Request.RequestUri.AbsolutePath.Should().Be("/api/sessions/scheduled-session/messages");
            Body.GetProperty("message").GetString().Should().Be("Summarize my garden notes");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { sessionId = "scheduled-session", response = $"Completed with {DefaultModel}" })
            };
        }
    }
}
