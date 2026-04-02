using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Worker.Consumers;

internal class AgentGeneratedTestMessageConsumer(ILogger<AgentGeneratedTestMessageConsumer> logger) : IConsumer<AgentGeneratedTestMessage>
{
    public Task Consume(ConsumeContext<AgentGeneratedTestMessage> context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = context.Message.CorrelationId,
            ["GeneratedBy"] = context.Message.GeneratedBy
        });

        logger.LogInformation(
            "Worker consumed agent-generated follow-up message: {Message}",
            context.Message.Message);

        return Task.CompletedTask;
    }
}
