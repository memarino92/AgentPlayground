using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Worker.Consumers;

internal class TestEventRequestedConsumer(ILogger<TestEventRequestedConsumer> logger) : IConsumer<TestEventRequested>
{
    public Task Consume(ConsumeContext<TestEventRequested> context)
    {
        logger.LogInformation(
            "Worker consumed test event {CorrelationId} from {Source} by {RequestedBy}: {Message}",
            context.Message.CorrelationId,
            context.Message.Source,
            context.Message.RequestedBy,
            context.Message.Message);

        return Task.CompletedTask;
    }
}
