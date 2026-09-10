using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using PersonalAgent.Services;

namespace PersonalAgent.Development;

// Stable token hashes exercise pgvector storage/querying; these are not semantic AI embeddings.
internal sealed class SyntheticEmbeddingService : IAgentEmbeddingService
{
    public static float[] Embed(string Content)
    {
        var Vector = new float[1536];
        foreach (Match Token in Regex.Matches(Content.ToLowerInvariant(), "[a-z0-9]+"))
        {
            var Hash = SHA256.HashData(Encoding.UTF8.GetBytes(Token.Value));
            var Index = ((Hash[0] << 8) | Hash[1]) % Vector.Length;
            Vector[Index] += 1;
        }
        if (Vector.All(Value => Value == 0)) Vector[0] = 1;
        var Norm = Math.Sqrt(Vector.Sum(Value => (double)Value * Value));
        return Vector.Select(Value => (float)(Value / Norm)).ToArray();
    }

    public Task<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string Content, CancellationToken CancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReadOnlyMemory<float>>(Embed(Content));
    }

    public async Task<List<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> Contents, CancellationToken CancellationToken = default)
    {
        var Result = new List<ReadOnlyMemory<float>>();
        foreach (var Content in Contents) Result.Add(await GenerateEmbeddingAsync(Content, CancellationToken));
        return Result;
    }
}
