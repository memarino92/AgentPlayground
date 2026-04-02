using AgentPlayground.Contracts.Messaging.Events;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Worker.Consumers;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public class WorkerConsumerHarnessTests
{
    [Fact]
    public async Task TestEventRequestedConsumer_ConsumesPublishedEvent()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x => x.AddConsumer<TestEventRequestedConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(new TestEventRequested
            {
                CorrelationId = Guid.NewGuid(),
                RequestedAt = DateTimeOffset.UtcNow,
                RequestedBy = "tester",
                Source = "test",
                Message = "run"
            });

            (await harness.Consumed.Any<TestEventRequested>()).Should().BeTrue();

            var consumerHarness = harness.GetConsumerHarness<TestEventRequestedConsumer>();
            (await consumerHarness.Consumed.Any<TestEventRequested>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task AgentGeneratedTestMessageConsumer_ConsumesPublishedEvent()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x => x.AddConsumer<AgentGeneratedTestMessageConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(new AgentGeneratedTestMessage
            {
                CorrelationId = Guid.NewGuid(),
                GeneratedAt = DateTimeOffset.UtcNow,
                GeneratedBy = "PersonalAgent",
                PromptSummary = "summary",
                Message = "follow-up"
            });

            (await harness.Consumed.Any<AgentGeneratedTestMessage>()).Should().BeTrue();

            var consumerHarness = harness.GetConsumerHarness<AgentGeneratedTestMessageConsumer>();
            (await consumerHarness.Consumed.Any<AgentGeneratedTestMessage>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }
}
