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

                opts.BaseUrl = FirstNonEmpty(
                    Environment.GetEnvironmentVariable("PERSONAL_AGENT_API_BASE_URL"),
                    configuration["services:personalagent-api:http:0"],
                    opts.BaseUrl);

                opts.InternalApiKey = FirstNonEmpty(
                    Environment.GetEnvironmentVariable("PERSONAL_AGENT_INTERNAL_API_KEY"),
                    configuration[$"{PersonalAgentApiOptions.SectionName}:InternalApiKey"],
                    configuration["Security:InternalApiKey"],
                    opts.InternalApiKey);
            })
            .Validate(opts => Uri.TryCreate(opts.BaseUrl, UriKind.Absolute, out _), $"{PersonalAgentApiOptions.SectionName}:BaseUrl must be an absolute URI")
            .Validate(opts => !string.IsNullOrWhiteSpace(opts.InternalApiKey), $"{PersonalAgentApiOptions.SectionName}:InternalApiKey is required")
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

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
