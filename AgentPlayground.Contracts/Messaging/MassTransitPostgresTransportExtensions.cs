using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentPlayground.Contracts.Messaging;

public static class MassTransitPostgresTransportExtensions
{
    public static void ConfigureSharedPostgresTransport(this IBusRegistrationConfigurator configurator)
    {
        configurator.SetKebabCaseEndpointNameFormatter();
        configurator.AddSqlMessageScheduler();

        configurator.UsingPostgres((context, cfg) =>
        {
            cfg.UsePostgres(context, host =>
            {
                var options = context.GetRequiredService<IOptions<MessagingOptions>>().Value;
                host.ConnectionString = options.ConnectionString;
                host.Schema = options.Schema;
            });

            cfg.UseSqlMessageScheduler();
            cfg.ConfigureEndpoints(context);
        });
    }
}
