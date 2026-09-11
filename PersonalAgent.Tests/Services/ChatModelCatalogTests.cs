using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class ChatModelCatalogTests
{
    [Fact]
    public async Task ShippedPolicy_ExposesNewerModelsOnlyWhenProviderMakesThemAvailable()
    {
        var Configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "chat-model-policy.json")).Build();
        var Policy = Configuration.GetSection(ChatModelCatalogOptions.SectionName).Get<ChatModelCatalogOptions>()!;
        var Clock = new TestClock();
        var Source = new Mock<IChatModelDiscovery>();
        string[] NewModels = ["gpt-5.5", "gpt-5.6-luna", "gpt-5.6-terra", "gpt-5.6-sol", "gpt-6-astra"];
        Source.SetupSequence(Value => Value.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["gpt-4o-mini"])
            .ReturnsAsync(["gpt-4o-mini", .. NewModels, "text-embedding-3-small", "gpt-realtime", "unknown-model"]);
        using var Catalog = Create(Source.Object, Clock, Policy);
        (await Catalog.GetModelsAsync()).Should().ContainSingle().Which.Id.Should().Be("gpt-4o-mini");
        Clock.Advance(300);
        var Models = await Catalog.GetModelsAsync();
        Models.Select(Value => Value.Id).Should().BeEquivalentTo(["gpt-4o-mini", .. NewModels]);
        Models.Should().ContainSingle(Value => Value.IsDefault).Which.Id.Should().Be("gpt-4o-mini");
        (await Catalog.FindModelAsync("gpt-6-astra"))!.Id.Should().Be("gpt-6-astra");
        Source.Verify(Value => Value.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Discovery_IntersectsConfiguredPolicy_AndSelectsOneDefault()
    {
        var source = new Mock<IChatModelDiscovery>();
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["model-a", "model-b", "embedding-model", "unreviewed-chat-model"]);
        using var catalog = Create(source.Object, new TestClock(), new ChatModelCatalogOptions
        {
            Models =
            [
                new() { Id = "unavailable", IsDefault = true },
                new() { Id = " model-b ", DisplayName = " Preferred model ", IsDefault = true },
                new() { Id = "MODEL-B", DisplayName = "Duplicate" },
                new() { Id = "model-a", IsDefault = true },
                new() { Id = "  " }
            ]
        });

        var models = await catalog.GetModelsAsync();

        models.Select(Model => Model.Id).Should().Equal("model-b", "model-a");
        models.Should().ContainSingle(Model => Model.IsDefault).Which.Id.Should().Be("model-b");
        models[0].DisplayName.Should().Be("Preferred model");
        (await catalog.FindModelAsync(" MODEL-A "))!.Id.Should().Be("model-a");
        (await catalog.FindModelAsync("embedding-model")).Should().BeNull();
        (await catalog.FindModelAsync(null))!.Id.Should().Be("model-b");
    }

    [Fact]
    public async Task Inventory_RefreshesAfterExpiry_WithoutChangingEarlierResponses()
    {
        var clock = new TestClock();
        var source = new Mock<IChatModelDiscovery>();
        source.SetupSequence(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["model-a"])
            .ReturnsAsync(["model-b"]);
        using var catalog = Create(source.Object, clock);

        var original = await catalog.GetModelsAsync();
        clock.Advance(299);
        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-a");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
        clock.Advance(1);

        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-b");
        (await catalog.FindModelAsync("model-a")).Should().BeNull();
        original.Should().ContainSingle().Which.Id.Should().Be("model-a");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ColdFailure_UsesConfiguredDefault_AndRetriesAfterBackoff()
    {
        var clock = new TestClock();
        var source = new Mock<IChatModelDiscovery>();
        source.SetupSequence(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("provider unavailable"))
            .ReturnsAsync(["model-b"]);
        using var catalog = Create(source.Object, clock);

        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-a");
        clock.Advance(29);
        (await catalog.GetModelsAsync()).Should().HaveCount(2);
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
        clock.Advance(1);
        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-b");
    }

    [Fact]
    public async Task RefreshFailure_PreservesLastKnownInventory_WithoutResurrectingRemovedModels()
    {
        var clock = new TestClock();
        var source = new Mock<IChatModelDiscovery>();
        source.SetupSequence(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["model-b"])
            .ThrowsAsync(new HttpRequestException());
        using var catalog = Create(source.Object, clock);
        await catalog.GetModelsAsync();
        clock.Advance(300);

        (await catalog.GetModelsAsync()).Should().ContainSingle().Which.Id.Should().Be("model-b");
        (await catalog.FindModelAsync("model-a")).Should().BeNull();
    }

    [Fact]
    public async Task SuccessfulEmptyInventory_RemainsEmptyDuringSubsequentFailure()
    {
        var clock = new TestClock();
        var source = new Mock<IChatModelDiscovery>();
        source.SetupSequence(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(["embedding-model"])
            .ThrowsAsync(new HttpRequestException());
        using var catalog = Create(source.Object, clock);
        (await catalog.GetModelsAsync()).Should().BeEmpty();
        clock.Advance(300);

        (await catalog.GetModelsAsync()).Should().BeEmpty();
        (await catalog.FindModelAsync(null)).Should().BeNull();
        await catalog.Invoking(Catalog => Catalog.GetDefaultModelAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneDiscoveryCall()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Mock<IChatModelDiscovery>();
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var catalog = Create(source.Object, new TestClock());

        var requests = Enumerable.Range(0, 12).Select(_ => catalog.GetModelsAsync()).ToArray();
        pending.SetResult(["model-b"]);
        var results = await Task.WhenAll(requests);

        results.Should().OnlyContain(Models => Models.Count == 1 && Models[0].Id == "model-b");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelledRefresh_PropagatesCancellation_AndAllowsNextRequestToRefresh()
    {
        var source = new Mock<IChatModelDiscovery>();
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken Token) => { await Task.Delay(Timeout.Infinite, Token); return new[] { "model-b" }; });
        using var catalog = Create(source.Object, new TestClock());
        using var cancellation = new CancellationTokenSource();

        var pending = catalog.GetModelsAsync(cancellation.Token);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["model-b"]);

        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-b");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelRefreshForOtherCallers()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Mock<IChatModelDiscovery>();
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>())).Returns(pending.Task);
        using var catalog = Create(source.Object, new TestClock());
        var first = catalog.GetModelsAsync();
        using var cancellation = new CancellationTokenSource();
        var waiter = catalog.GetModelsAsync(cancellation.Token);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => waiter).Should().ThrowAsync<OperationCanceledException>();
        pending.SetResult(["model-b"]);

        (await first).Should().ContainSingle().Which.Id.Should().Be("model-b");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DiscoveryTimeout_ReturnsFallback_AndCancelsProviderRequest()
    {
        var source = new Mock<IChatModelDiscovery>();
        var providerToken = CancellationToken.None;
        source.Setup(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken Token) =>
            {
                providerToken = Token;
                await Task.Delay(Timeout.Infinite, Token);
                return Array.Empty<string>();
            });
        using var catalog = Create(source.Object, new TestClock(), Defaults() with { DiscoveryTimeoutSeconds = 1 });

        var models = await catalog.GetModelsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        models.Should().HaveCount(2);
        providerToken.IsCancellationRequested.Should().BeTrue();
        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-a");
        source.Verify(Source => Source.GetModelIdsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfiguredMode_DoesNotContactProvider()
    {
        var source = new Mock<IChatModelDiscovery>(MockBehavior.Strict);
        using var catalog = Create(source.Object, new TestClock(), Defaults() with { DiscoverFromProvider = false });

        (await catalog.GetDefaultModelAsync()).Id.Should().Be("model-a");
        source.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task EmptyConfiguration_PreservesApiOwnedFallback()
    {
        using var catalog = Create(Mock.Of<IChatModelDiscovery>(), new TestClock(), new ChatModelCatalogOptions { DiscoverFromProvider = false });
        (await catalog.GetModelsAsync()).Should().ContainSingle().Which.IsDefault.Should().BeTrue();
    }

    private static ChatModelCatalog Create(IChatModelDiscovery Source, TimeProvider Clock, ChatModelCatalogOptions? Options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(Options ?? Defaults()), Source, Clock, NullLogger<ChatModelCatalog>.Instance);

    private static ChatModelCatalogOptions Defaults() => new()
    {
        Models = [new() { Id = "model-a", IsDefault = true }, new() { Id = "model-b" }]
    };

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int Seconds) => _now = _now.AddSeconds(Seconds);
    }
}
