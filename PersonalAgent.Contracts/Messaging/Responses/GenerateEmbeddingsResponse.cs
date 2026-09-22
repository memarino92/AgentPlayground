namespace PersonalAgent.Contracts.Messaging.Responses;

public record GenerateEmbeddingsResponse(List<float[]> Embeddings, int Dimensions);
