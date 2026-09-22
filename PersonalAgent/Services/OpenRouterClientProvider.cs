using Microsoft.Extensions.Options;
using OpenAI;
using System.ClientModel;

using PersonalAgent.Configuration;

namespace PersonalAgent.Services;

internal sealed class OpenRouterClientProvider(IOptions<ApiKeyOptions> Options)
{
    internal static readonly Uri Endpoint = new("https://openrouter.ai/api/v1");
    private readonly object Gate = new();
    private string? Key;
    private OpenAIClient? Client;

    public OpenAIClient Current
    {
        get
        {
            lock (Gate)
            {
                var key = Options.Value.OpenRouterKey;
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException("OpenRouter:ApiKey is not configured.");
                if (Client is not null && Key == key) return Client;
                var replacement = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = Endpoint });
                Key = key;
                return Client = replacement;
            }
        }
    }
}
