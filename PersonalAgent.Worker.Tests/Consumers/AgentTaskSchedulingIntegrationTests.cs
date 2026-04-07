using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Commands;
using AgentPlayground.Contracts.Messaging.Events;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Worker.Consumers;
using Xunit;

namespace PersonalAgent.Worker.Tests.Consumers;

public class AgentTaskSchedulingIntegrationTests
{
    [Fact]
    public async Task AgentTaskScheduled_Immediate_ExecutesAndPublishesCompletionNotification()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddDelayedMessageScheduler();
                x.AddConsumer<AgentTaskSchedulerConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.AgentTaskScheduler);
                x.AddConsumer<AgentTaskExecutorConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.AgentTaskExecutor);
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(new AgentTaskScheduled
            {
                TaskId = Guid.NewGuid(),
                TenantId = "tenant-a",
                UserId = "user-1",
                CorrelationId = Guid.NewGuid(),
                RequestedAtUtc = DateTimeOffset.UtcNow,
                ExecuteAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                Instruction = "check status",
                NotifyOnCompletion = true
            });

            (await harness.Consumed.Any<AgentTaskScheduled>()).Should().BeTrue();
            (await harness.Consumed.Any<ExecuteAgentTask>()).Should().BeTrue();
            (await harness.Published.Any<NotificationRequested>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task AgentTaskScheduled_Delayed_ExecutesAfterDelay()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddDelayedMessageScheduler();
                x.AddConsumer<AgentTaskSchedulerConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.AgentTaskScheduler);
                x.AddConsumer<AgentTaskExecutorConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.AgentTaskExecutor);
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            await harness.Bus.Publish(new AgentTaskScheduled
            {
                TaskId = Guid.NewGuid(),
                TenantId = "tenant-a",
                UserId = "user-1",
                CorrelationId = Guid.NewGuid(),
                RequestedAtUtc = DateTimeOffset.UtcNow,
                ExecuteAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250),
                Instruction = "look up score",
                NotifyOnCompletion = true
            });

            (await harness.Consumed.Any<ExecuteAgentTask>()).Should().BeTrue();
            (await harness.Published.Any<NotificationRequested>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }
}
