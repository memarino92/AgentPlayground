using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PersonalAgent.Configuration;
using PersonalAgent.Services;
using Xunit;

namespace PersonalAgent.Tests.Services;

public class SemanticMemoryServiceTests
{
    [Fact]
    public async Task RecallMemoriesAsync_FiltersByDistanceAndDeduplicates()
    {
        var embeddingService = new Mock<IAgentEmbeddingService>();
        embeddingService
            .Setup(service => service.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadOnlyMemory<float>([0.1f, 0.2f]));

        var memoryStore = new Mock<IAgentSemanticMemoryStore>();
        memoryStore
            .Setup(store => store.SearchMemoriesAsync("profile", It.IsAny<ReadOnlyMemory<float>>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new MemoryRecord(1, "loves coffee", 0.22d),
                new MemoryRecord(2, "loves coffee", 0.24d),
                new MemoryRecord(3, "too far", 0.81d),
                new MemoryRecord(4, "my message", 0.18d)
            ]);

        var options = Options.Create(new AgentMemoryOptions { EnableSemanticMemory = true });
        var service = new SemanticMemoryService(embeddingService.Object, memoryStore.Object, options, NullLogger<SemanticMemoryService>.Instance);

        var result = await service.RecallMemoriesAsync("profile", "my message");

        result.Should().BeEquivalentTo(["loves coffee"]);
    }
}
