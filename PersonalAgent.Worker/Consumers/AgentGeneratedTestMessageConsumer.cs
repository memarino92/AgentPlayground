using AgentPlayground.Contracts.Messaging.Events;
using MassTransit;

namespace PersonalAgent.Worker.Consumers;

internal class AgentGeneratedTestMessageConsumer(ILogger<AgentGeneratedTestMessageConsumer> logger) : IConsumer<AgentGeneratedTestMessage>
{
    public Task Consume(ConsumeContext<AgentGeneratedTestMessage> context)
    {
        logger.LogInformation(
            "Worker consumed agent-generated message {CorrelationId} from {GeneratedBy}: {Message}",
            context.Message.CorrelationId,
            context.Message.GeneratedBy,
            context.Message.Message);

        return Task.CompletedTask;
    }
}
