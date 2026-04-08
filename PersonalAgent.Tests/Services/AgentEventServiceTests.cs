using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class AgentEventServiceTests
{
    private static AgentEventService CreateService()
    {
        var apiKeyOptions = Options.Create(new ApiKeyOptions { OpenAiKey = "test-key" });
        var bus = new Mock<IBus>().Object;
        var chatModelCatalog = new ChatModelCatalog(Options.Create(new ChatModelCatalogOptions
        {
            Models = [new ChatModelOption { Id = "gpt-4o-mini", DisplayName = "GPT-4o Mini", IsDefault = true }]
        }));
        var schedulingService = new SchedulingService(new Mock<IBus>().Object, NullLogger<SchedulingService>.Instance);
        return new AgentEventService(
            apiKeyOptions,
            bus,
            chatModelCatalog,
            schedulingService,
            NullLogger<AgentEventService>.Instance,
            NullLoggerFactory.Instance,
            new Mock<IServiceProvider>().Object);
    }

    [Fact]
    public async Task GetCurrentDateTimeToolAsync_NoTimezone_ReturnsUtcString()
    {
        var before = DateTimeOffset.UtcNow;
        var service = CreateService();

        var result = await service.GetCurrentDateTimeToolAsync();

        var after = DateTimeOffset.UtcNow;
        result.Should().Contain("UTC");
        result.Should().Contain("Current UTC date and time:");

        // The returned ISO-8601 timestamp should fall within our test window
        var isoPrefix = "(ISO-8601: ";
        var isoStart = result.IndexOf(isoPrefix, StringComparison.Ordinal) + isoPrefix.Length;
        var isoEnd = result.IndexOf(')', isoStart);
        var isoString = result[isoStart..isoEnd];
        var parsed = DateTimeOffset.Parse(isoString);
        parsed.Should().BeOnOrAfter(before.AddSeconds(-1));
        parsed.Should().BeOnOrBefore(after.AddSeconds(1));
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
