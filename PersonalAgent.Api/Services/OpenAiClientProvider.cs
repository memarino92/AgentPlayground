using Microsoft.Extensions.Options;
using OpenAI;
using PersonalAgent.Api.Configuration;

namespace PersonalAgent.Api.Services;

internal sealed class OpenAiClientProvider(IOptions<ApiKeyOptions> Options)
{
    private readonly object Gate = new();
    private string? Key;
    private OpenAIClient? Client;

    public OpenAIClient Current
    {
        get
        {
            lock (Gate)
            {
                var key = Options.Value.OpenAiKey;
                if (Client is not null && Key == key) return Client;
                var replacement = new OpenAIClient(key);
                Key = key;
                return Client = replacement;
            }
        }
    }
}
