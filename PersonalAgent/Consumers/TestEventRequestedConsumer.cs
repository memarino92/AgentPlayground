using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using PersonalAgent.Services;

namespace PersonalAgent.Consumers;

internal class TestEventRequestedConsumer(AgentEventService agentEventService, ILogger<TestEventRequestedConsumer> logger) : IConsumer<TestEventRequested>
{
    public async Task Consume(ConsumeContext<TestEventRequested> context)
    {
        logger.LogInformation(
            "Agent consumed test event {CorrelationId} from {Source} by {RequestedBy}",
            context.Message.CorrelationId,
            context.Message.Source,
            context.Message.RequestedBy);

        await agentEventService.GenerateAndPublishTestMessageAsync(context.Message, context.CancellationToken);
    }
}
