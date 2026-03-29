using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentPlayground.Contracts.Messaging;

public static class MessagingOptionsServiceCollectionExtensions
{
    public static IServiceCollection AddMessagingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MessagingOptions>()
            .Bind(configuration.GetSection(MessagingOptions.SectionName))
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ConnectionString), $"{MessagingOptions.SectionName}:ConnectionString is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.Schema), $"{MessagingOptions.SectionName}:Schema is required")
            .ValidateOnStart();

        return services;
    }
}
