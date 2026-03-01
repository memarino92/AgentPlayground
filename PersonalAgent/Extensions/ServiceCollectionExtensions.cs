using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PersonalAgent.Configuration;
using PersonalAgent.Services;

namespace PersonalAgent.Extensions;

internal static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPersonalAgentServices(this IServiceCollection services, IConfiguration configuration)
    {
        var securityOptions = configuration.GetSection("Security").Get<SecurityOptions>() ?? new SecurityOptions();

        services.AddOptions<ApiKeyOptions>()
            .Configure(opts =>
            {
                opts.OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? configuration["OpenApiKey"]
                    ?? string.Empty;
                opts.InternalApiKey = Environment.GetEnvironmentVariable("INTERNAL_API_KEY")
                    ?? configuration["Security:InternalApiKey"]
                    ?? string.Empty;
            });
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection("Security"));
        services.AddSingleton<AgentService>();
        services.AddEndpointsApiExplorer();
        services.AddCors(options =>
        {
            options.AddPolicy(PersonalAgentConstants.ApiCorsPolicy, policy =>
            {
                if (securityOptions.AllowedOrigins.Length is 0) return;
                policy.WithOrigins(securityOptions.AllowedOrigins).AllowAnyMethod().AllowAnyHeader();
            });
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(PersonalAgentConstants.ApiRateLimiter, limiter =>
            {
                limiter.PermitLimit = securityOptions.RateLimit.PermitLimit;
                limiter.Window = TimeSpan.FromSeconds(securityOptions.RateLimit.WindowSeconds);
                limiter.QueueLimit = 0;
            });
        });

        return services;
    }
}
