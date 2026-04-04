using AgentPlayground.Contracts.Messaging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;
using PersonalAgent.Web.DataProtection;
using PersonalAgent.Web.Services;

namespace PersonalAgent.Web.Extensions;

internal static class ServiceCollectionDataProtectionExtensions
{
    public static IServiceCollection AddPostgresDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMessagingOptions(configuration);
        services.AddSingleton(serviceProvider =>
        {
            var messagingOptions = serviceProvider.GetRequiredService<IOptions<MessagingOptions>>().Value;
            return new PostgresXmlRepository(messagingOptions.ConnectionString);
        });
        services.AddOptions<KeyManagementOptions>()
            .Configure<PostgresXmlRepository>((options, repository) => options.XmlRepository = repository);
        services.AddDataProtection().SetApplicationName("PersonalAgent.Web");
        services.AddHostedService<DataProtectionKeyTableInitializer>();
        return services;
    }
}
