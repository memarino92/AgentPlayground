using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;
using PersonalAgent.Services;

namespace PersonalAgent.Consumers;

internal class TestEventRequestedConsumer(AgentService agentService, ILogger<TestEventRequestedConsumer> logger) : IConsumer<TestEventRequested>
{
    public async Task Consume(ConsumeContext<TestEventRequested> context)
    {
        logger.LogInformation(
            "Agent consumed test event {CorrelationId} from {Source} by {RequestedBy}",
            context.Message.CorrelationId,
            context.Message.Source,
            context.Message.RequestedBy);

        await agentService.GenerateAndPublishTestMessageAsync(context.Message, context.CancellationToken);
    }
}
