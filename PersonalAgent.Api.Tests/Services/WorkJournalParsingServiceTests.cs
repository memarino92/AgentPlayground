using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PersonalAgent.Api.Models;
using PersonalAgent.Api.Services;
using Xunit;

namespace PersonalAgent.Api.Tests.Services;

public sealed class WorkJournalParsingServiceTests
{
    [Fact]
    public async Task Parsing_UsesProviderAwareFactory_ForCatalogDefault()
    {
        const string modelId = "openrouter:openrouter/auto";
        var catalog = new Mock<IChatModelCatalog>();
        catalog.Setup(Value => Value.GetDefaultModelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AvailableChatModel(modelId, "OpenRouter Auto", true));
        var client = new Mock<IChatClient>();
        client.Setup(Value => Value.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(),
                It.Is<ChatOptions>(Options => Options.ResponseFormat == ChatResponseFormat.Json), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                "{\"entries\":[{\"date\":\"2026-09-22\",\"content\":\"## 09/22\\n- Shipped routing\"}]}")));
        var factory = new Mock<IAgentChatClientFactory>();
        factory.Setup(Value => Value.Create(modelId)).Returns(client.Object);
        var service = new WorkJournalParsingService(factory.Object, catalog.Object,
            NullLogger<WorkJournalParsingService>.Instance);

        var entries = await service.ParseEntriesAsync("2026_09.md", "## 09/22\n- Shipped routing");

        entries.Should().ContainSingle().Which.Date.Should().Be(new DateTime(2026, 9, 22));
        factory.VerifyAll();
    }
}
