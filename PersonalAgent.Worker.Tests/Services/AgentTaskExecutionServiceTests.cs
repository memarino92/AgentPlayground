using System.Net;
using System.Net.Http.Json;
using AgentPlayground.Contracts.Messaging.Commands;
using FluentAssertions;
using Moq;
using PersonalAgent.Worker.Services;
using Xunit;

namespace PersonalAgent.Worker.Tests.Services;

public class AgentTaskExecutionServiceTests
{
    [Fact]
    public async Task ExecuteAsync_UsesJobIdentityWithoutImpersonatingOwner()
    {
        var Id = Guid.NewGuid();
        using var Handler = new JobHandler(Id, HttpStatusCode.OK);
        using var Client = new HttpClient(Handler) { BaseAddress = new Uri("http://api.example.test") };
        var Factory = new Mock<IHttpClientFactory>();
        Factory.Setup(F => F.CreateClient("PersonalAgentApi")).Returns(Client);
        var Result = await new AgentTaskExecutionService(Factory.Object).ExecuteAsync(Delivery(Id));
        Result.Succeeded.Should().BeTrue();
        Result.Summary.Should().Be("Saved result");
        Handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task HttpFailureIsRetryableInsteadOfNormallyCompletingConsumer()
    {
        var Id = Guid.NewGuid();
        using var Handler = new JobHandler(Id, HttpStatusCode.ServiceUnavailable);
        using var Client = new HttpClient(Handler) { BaseAddress = new Uri("http://api.example.test") };
        var Factory = new Mock<IHttpClientFactory>();
        Factory.Setup(F => F.CreateClient("PersonalAgentApi")).Returns(Client);
        var Act = () => new AgentTaskExecutionService(Factory.Object).ExecuteAsync(Delivery(Id));
        await Act.Should().ThrowAsync<HttpRequestException>();
    }

    private static ExecuteAgentTask Delivery(Guid Id) => new()
    {
        TaskId = Id, CorrelationId = Guid.NewGuid(), TenantId = "default", UserId = "owner",
        ExecuteAtUtc = DateTimeOffset.UtcNow, Instruction = "legacy instruction"
    };

    private sealed class JobHandler(Guid Id, HttpStatusCode Status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken Token)
        {
            RequestCount++;
            Request.RequestUri!.AbsolutePath.Should().Be($"/api/jobs/{Id}/execute");
            Request.Headers.Contains("X-Agent-Role").Should().BeFalse();
            Request.Headers.Contains("X-Agent-Signature").Should().BeFalse();
            (await Request.Content!.ReadFromJsonAsync<ExecuteAgentTask>(Token))!.TaskId.Should().Be(Id);
            return new(Status) { Content = JsonContent.Create(new { status = "Completed", summary = "Saved result" }) };
        }
    }
}
