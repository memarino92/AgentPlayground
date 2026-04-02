using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Web.Services;

internal class TestEventPublisher(IPublishEndpoint publishEndpoint, ILogger<TestEventPublisher> logger)
{
    public async Task<Guid> PublishAsync(string requestedBy, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid();
        var normalizedRequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "anonymous" : requestedBy;

        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["RequestedBy"] = normalizedRequestedBy,
            ["Source"] = "PersonalAgent.Web"
        });

        await publishEndpoint.Publish(new TestEventRequested
        {
            CorrelationId = correlationId,
            RequestedAt = DateTimeOffset.UtcNow,
            RequestedBy = normalizedRequestedBy,
            Source = "PersonalAgent.Web",
            Message = "Frontend test event fired from the web UI"
        }, cancellationToken);

        logger.LogInformation("Published web test event to shared bus");

        return correlationId;
    }
}
