using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Web.Services;

internal class TestEventPublisher(IPublishEndpoint publishEndpoint)
{
    public async Task<Guid> PublishAsync(string requestedBy, CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid();

        await publishEndpoint.Publish(new TestEventRequested
        {
            CorrelationId = correlationId,
            RequestedAt = DateTimeOffset.UtcNow,
            RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "anonymous" : requestedBy,
            Source = "PersonalAgent.Web",
            Message = "Frontend test event fired from the web UI"
        }, cancellationToken);

        return correlationId;
    }
}
