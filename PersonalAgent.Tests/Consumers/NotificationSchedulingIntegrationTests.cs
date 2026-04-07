using AgentPlayground.Contracts.Messaging;
using AgentPlayground.Contracts.Messaging.Commands;
using AgentPlayground.Contracts.Messaging.Events;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using PersonalAgent.Consumers;
using Xunit;

namespace PersonalAgent.Tests.Consumers;

public class NotificationSchedulingIntegrationTests
{
    [Fact]
    public async Task NotificationRequested_Immediate_SendsNotificationCommand()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddDelayedMessageScheduler();
                x.AddConsumer<NotificationSchedulerConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.NotificationScheduler);
                x.AddConsumer<SendNotificationCaptureConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.PushNotification);
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
            await harness.Bus.Publish(new NotificationRequested
            {
                NotificationId = Guid.NewGuid(),
                TenantId = "tenant-a",
                UserId = "user-1",
                CorrelationId = Guid.NewGuid(),
                RequestedAtUtc = DateTimeOffset.UtcNow,
                ExecuteAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                Title = "Now",
                Body = "Immediate",
                Source = "Test"
            });

            (await harness.Consumed.Any<NotificationRequested>()).Should().BeTrue();
            (await harness.Consumed.Any<SendNotification>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task NotificationRequested_Delayed_SendsNotificationCommand()
    {
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddDelayedMessageScheduler();
                x.AddConsumer<NotificationSchedulerConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.NotificationScheduler);
                x.AddConsumer<SendNotificationCaptureConsumer>().Endpoint(e => e.Name = MessagingEndpointNames.PushNotification);
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
            await harness.Bus.Publish(new NotificationRequested
            {
                NotificationId = Guid.NewGuid(),
                TenantId = "tenant-a",
                UserId = "user-1",
                CorrelationId = Guid.NewGuid(),
                RequestedAtUtc = DateTimeOffset.UtcNow,
                ExecuteAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250),
                Title = "Later",
                Body = "Delayed",
                Source = "Test"
            });

            (await harness.Consumed.Any<SendNotification>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }

    private sealed class SendNotificationCaptureConsumer : IConsumer<SendNotification>
    {
        public Task Consume(ConsumeContext<SendNotification> context) => Task.CompletedTask;
    }
}
