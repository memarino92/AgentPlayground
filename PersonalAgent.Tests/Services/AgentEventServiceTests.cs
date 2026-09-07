using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class AgentEventServiceTests
{
    private static AgentEventService CreateService()
    {
        var bus = new Mock<IBus>().Object;
        var schedulingService = new SchedulingService(bus, NullLogger<SchedulingService>.Instance);
        return new AgentEventService(bus, schedulingService, NullLogger<AgentEventService>.Instance);
    }

    [Fact]
    public async Task GetCurrentDateTimeToolAsync_NoTimezone_DefaultsToEasternAndIncludesUtc()
    {
        var service = CreateService();

        var result = await service.GetCurrentDateTimeToolAsync();

        // Should include both a local Eastern time line and a UTC line
        result.Should().Contain("Current date and time in");
        result.Should().Contain("Current UTC date and time:");
        // Eastern timezone display name contains "Eastern" on all platforms
        result.Should().Contain("Eastern");
    }

    [Fact]
    public async Task GetCurrentDateTimeToolAsync_WithValidTimezone_ReturnsLocalAndUtcStrings()
    {
        var service = CreateService();

        var result = await service.GetCurrentDateTimeToolAsync("UTC");

        result.Should().Contain("Current date and time in");
        result.Should().Contain("Current UTC date and time:");
    }

    [Fact]
    public async Task GetCurrentDateTimeToolAsync_WithInvalidTimezone_FallsBackToUtc()
    {
        var service = CreateService();

        var result = await service.GetCurrentDateTimeToolAsync("Not/AReal_Zone");

        result.Should().Contain("UTC");
        result.Should().Contain("was not recognized");
    }
}
