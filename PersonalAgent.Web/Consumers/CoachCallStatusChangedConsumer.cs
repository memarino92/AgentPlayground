using AgentPlayground.Contracts.Events;
using MassTransit;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Consumers;

public sealed class CoachCallStatusChangedConsumer(CoachCallUpdates Updates) : IConsumer<CoachCallStatusChangedEvent>
{
    public Task Consume(ConsumeContext<CoachCallStatusChangedEvent> Context)
    {
        Updates.Notify(Context.Message);
        return Task.CompletedTask;
    }
}
