namespace AgentPlayground.Contracts.Messaging.Requests;

public record GenerateEmbeddingsRequest(Guid CorrelationId, string Source, List<string> Inputs);
