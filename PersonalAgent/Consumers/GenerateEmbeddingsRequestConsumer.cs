using AgentPlayground.Contracts.Messaging.Requests;
using AgentPlayground.Contracts.Messaging.Responses;
using MassTransit;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Services;

namespace PersonalAgent.Consumers;

internal class GenerateEmbeddingsRequestConsumer(
    IAgentEmbeddingService embeddingService,
    IOptions<AgentMemoryOptions> memoryOptions,
    ILogger<GenerateEmbeddingsRequestConsumer> logger) : IConsumer<GenerateEmbeddingsRequest>
{
    public async Task Consume(ConsumeContext<GenerateEmbeddingsRequest> context)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = context.Message.CorrelationId,
            ["Source"] = context.Message.Source,
            ["InputCount"] = context.Message.Inputs.Count
        });

        if (context.Message.Inputs.Count == 0)
        {
            logger.LogInformation("Received embedding request with no inputs");
            await context.RespondAsync(new GenerateEmbeddingsResponse([], memoryOptions.Value.VectorDimensions, context.Message.Model ?? memoryOptions.Value.EmbeddingModel));
            return;
        }

        logger.LogInformation("Generating embeddings for {InputCount} inputs", context.Message.Inputs.Count);
        var vectors = await embeddingService.GenerateEmbeddingsAsync(context.Message.Inputs, context.CancellationToken);
        logger.LogInformation("Generated embeddings for {InputCount} inputs", vectors.Count);

        await context.RespondAsync(new GenerateEmbeddingsResponse(
            vectors.Select(vector => vector.ToArray()).ToList(),
            memoryOptions.Value.VectorDimensions,
            context.Message.Model ?? memoryOptions.Value.EmbeddingModel));
    }
}
