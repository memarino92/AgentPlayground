namespace PersonalAgent.Services;

internal sealed class ScheduledJobReconciler(ScheduledJobStore Store, ILogger<ScheduledJobReconciler> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            try { await Store.ReconcileAsync(StoppingToken); }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (Exception Exception) { Logger.LogWarning(Exception, "Scheduled jobs reconciliation will retry"); }
            await Task.Delay(TimeSpan.FromSeconds(15), StoppingToken);
        }
    }
}
