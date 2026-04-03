using AgentPlayground.Contracts.Commands;
using MassTransit;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PersonalAgent.Worker.Services;

public class WeeklyWorkJournalSyncService(
    IBus bus,
    ILogger<WeeklyWorkJournalSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Weekly work journal sync service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var nextRun = GetNextRunTime(now);
            var delay = nextRun - now;

            logger.LogInformation("Next work journal sync scheduled for {NextRun} (in {DelayHours:N2} hours).", nextRun, delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Triggering weekly work journal sync.");
                await bus.Publish(new SyncWorkJournalCommand(), stoppingToken);
                
                // Wait briefly to avoid triggering multiple times if the loop runs very quickly
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private static DateTimeOffset GetNextRunTime(DateTimeOffset now)
    {
        var daysUntilSunday = ((int)DayOfWeek.Sunday - (int)now.DayOfWeek + 7) % 7;
        var nextSunday = now.Date.AddDays(daysUntilSunday);
        var nextRun = new DateTimeOffset(nextSunday.AddHours(2), TimeSpan.Zero); // Sunday 2:00 AM UTC

        // If today is Sunday but it's already past 2 AM, schedule for next week
        if (nextRun <= now)
        {
            nextRun = nextRun.AddDays(7);
        }

        return nextRun;
    }
}
