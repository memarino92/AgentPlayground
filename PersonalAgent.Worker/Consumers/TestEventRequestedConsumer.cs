using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Worker.Consumers;

internal class TestEventRequestedConsumer(ILogger<TestEventRequestedConsumer> logger) : IConsumer<TestEventRequested>
{
    public Task Consume(ConsumeContext<TestEventRequested> context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = context.Message.CorrelationId,
            ["RequestedBy"] = context.Message.RequestedBy,
            ["Source"] = context.Message.Source
        });

        logger.LogInformation(
            "Worker consumed requested test event with message: {Message}",
            context.Message.Message);

        return Task.CompletedTask;
    }
}
