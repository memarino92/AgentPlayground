using AgentPlayground.Contracts.Messaging;
using MassTransit;
using Microsoft.Extensions.Options;
using PersonalAgent.Worker.Configuration;

namespace PersonalAgent.Worker.Services;

internal sealed class CoachCallOutboxDispatcher(
    IOptions<SqlTransportOptions> SqlOptions,
    IOptions<CoachCheckinWorkerOptions> Options,
    IBus Bus,
    ILogger<CoachCallOutboxDispatcher> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await CoachCallOutbox.DispatchOneAsync(SqlOptions.Value.ConnectionString!, Options.Value.Schema,
                    (Id, Message, Token) => CoachCallOutbox.DeliverAsync(Bus, Id, Message, Token), StoppingToken)) continue;
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (Exception Exception) { Logger.LogError(Exception, "Coach call outbox delivery failed; pending messages will be retried"); }
            await Task.Delay(TimeSpan.FromSeconds(2), StoppingToken);
        }
    }
}
