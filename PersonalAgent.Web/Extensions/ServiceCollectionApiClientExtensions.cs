using AgentPlayground.Contracts.Configuration;
using PersonalAgent.Web.Configuration;
using PersonalAgent.Web.Services;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Web.Extensions;

internal static class ServiceCollectionApiClientExtensions
{
    public static IServiceCollection AddPersonalAgentApiClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PersonalAgentApiOptions>()
            .Configure(opts =>
            {
                configuration.GetSection(PersonalAgentApiOptions.SectionName).Bind(opts);

                opts.BaseUrl = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "PERSONAL_AGENT_API_BASE_URL",
                    $"{PersonalAgentApiOptions.SectionName}:BaseUrl",
                    opts.BaseUrl)
                    ?? opts.BaseUrl;

                opts.InternalApiKey = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "INTERNAL_API_KEY",
                    $"{PersonalAgentApiOptions.SectionName}:InternalApiKey",
                    opts.InternalApiKey)
                    ?? opts.InternalApiKey;
                opts.ActorSigningKey = ConfigurationValueResolver.ResolveString(
                    configuration,
                    "WEB_ACTOR_SIGNING_KEY",
                    $"{PersonalAgentApiOptions.SectionName}:ActorSigningKey",
                    opts.ActorSigningKey)
                    ?? opts.ActorSigningKey;
            })
            .Validate(opts => Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out _), $"{PersonalAgentApiOptions.SectionName}:BaseUrl must be an absolute URI")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.InternalApiKey), $"{PersonalAgentApiOptions.SectionName}:InternalApiKey is required")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.ActorSigningKey), $"{PersonalAgentApiOptions.SectionName}:ActorSigningKey is required")
            .ValidateOnStart();

        services.AddHttpClient<PersonalAgentClient>((serviceProvider, client) =>
        {
            var personalAgentApiOptions = serviceProvider.GetRequiredService<IOptions<PersonalAgentApiOptions>>().Value;

            client.BaseAddress = new Uri(personalAgentApiOptions.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);

            if (!string.IsNullOrWhiteSpace(personalAgentApiOptions.InternalApiKey))
                client.DefaultRequestHeaders.Add("X-Internal-Api-Key", personalAgentApiOptions.InternalApiKey);
        });

        return services;
    }
}
