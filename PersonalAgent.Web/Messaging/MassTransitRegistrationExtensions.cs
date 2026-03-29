using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;

namespace PersonalAgent.Web.Messaging;

internal static class MassTransitRegistrationExtensions
{
    public static void ConfigureSharedPostgresTransport(this IBusRegistrationConfigurator configurator)
    {
        configurator.SetKebabCaseEndpointNameFormatter();

        configurator.UsingPostgres((context, cfg) =>
        {
            cfg.UsePostgres(context, host =>
            {
                var options = context.GetRequiredService<IOptions<MessagingOptions>>().Value;
                host.ConnectionString = options.ConnectionString;
                host.Schema = options.Schema;
            });

            cfg.ConfigureEndpoints(context);
        });
    }
}
