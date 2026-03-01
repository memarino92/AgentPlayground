using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;

namespace PersonalAgent.Security;

internal class InternalApiKeyFilter(IOptions<ApiKeyOptions> apiKeyOptions) : IEndpointFilter
{
    private readonly string? _apiKey = string.IsNullOrWhiteSpace(apiKeyOptions.Value.InternalApiKey) ? null : apiKeyOptions.Value.InternalApiKey;

    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (_apiKey is null) return next(context);

        var hasHeader = context.HttpContext.Request.Headers.TryGetValue("X-Internal-Api-Key", out var providedApiKey);
        if (!hasHeader || !string.Equals(_apiKey, providedApiKey.ToString(), StringComparison.Ordinal))
            return ValueTask.FromResult<object?>(Results.Unauthorized());

        return next(context);
    }
}
