using MassTransit;
using Microsoft.Extensions.Options;
using Npgsql;
using PersonalAgent.Api.Configuration;
using PersonalAgent.Contracts.Messaging.Commands;

namespace PersonalAgent.Api.Services;

internal sealed class CoachCallSummaryBackfill(
    IOptions<AgentMemoryOptions> Options,
    IBus Sender,
    ILogger<CoachCallSummaryBackfill> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(StoppingToken);
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { return; }
            catch (Exception Ex) { Logger.LogError(Ex, "Coach call summary backfill failed; it will retry"); }
            await Task.Delay(TimeSpan.FromMinutes(2), StoppingToken);
        }
    }

    internal async Task SweepAsync(CancellationToken Token)
    {
        var Schema = new NpgsqlCommandBuilder().QuoteIdentifier(Options.Value.Schema);
        var Missing = new List<GenerateCoachCallSummary>();
        var Workflows = new List<StartCoachCallWorkflow>();
        var Signals = new List<CoachCallWorkflowSignal>();
        await using (var Connection = new NpgsqlConnection(Options.Value.ConnectionString))
        {
            await Connection.OpenAsync(Token);
            await using (var Command = new NpgsqlCommand($"""
                SELECT u.upload_id, s.session_id, u.profile_id
                FROM {Schema}.coach_call_uploads u JOIN {Schema}.coach_call_sessions s ON s.upload_id = u.upload_id
                WHERE u.status = 'Completed'
                  AND s.summary_json->>'executiveSummaryVersion' IS DISTINCT FROM '1'
                ORDER BY u.created_at LIMIT 25
                """, Connection))
            await using (var Reader = await Command.ExecuteReaderAsync(Token))
                while (await Reader.ReadAsync(Token))
                    Missing.Add(new GenerateCoachCallSummary(Reader.GetGuid(0), Reader.GetGuid(1), Reader.GetString(2)));
            await using (var Command = new NpgsqlCommand($"""
                SELECT u.upload_id, u.session_id, u.profile_id, u.correlation_id, u.status
                FROM {Schema}.coach_call_uploads u
                WHERE u.status IN ('Uploaded', 'Queued', 'Transcribing', 'AwaitingSpeakerOverride', 'Processing')
                  AND u.session_id IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM automation."CoachCallWorkflows" w WHERE w."CorrelationId" = u.upload_id)
                ORDER BY u.created_at LIMIT 25
                """, Connection))
            await using (var Reader = await Command.ExecuteReaderAsync(Token))
                while (await Reader.ReadAsync(Token))
                    Workflows.Add(new StartCoachCallWorkflow(Reader.GetGuid(0), Reader.GetGuid(1), Reader.GetString(2), Reader.GetGuid(3), Reader.GetString(4)));
            await using (var Command = new NpgsqlCommand($"""
                SELECT u.upload_id, u.session_id, u.profile_id, u.status, w."CurrentState",
                       s.summary_json->>'executiveSummaryVersion'
                FROM {Schema}.coach_call_uploads u
                JOIN {Schema}.coach_call_sessions s ON s.session_id = u.session_id
                JOIN automation."CoachCallWorkflows" w ON w."CorrelationId" = u.upload_id
                WHERE w."CurrentState" NOT IN ('Completed', 'Failed')
                ORDER BY u.created_at LIMIT 100
                """, Connection))
            await using (var Reader = await Command.ExecuteReaderAsync(Token))
                while (await Reader.ReadAsync(Token))
                {
                    var Domain = Reader.GetString(3);
                    var Workflow = Reader.GetString(4);
                    var Version = Reader.IsDBNull(5) ? null : Reader.GetString(5);
                    var Stage = (Domain, Workflow, Version) switch
                    {
                        ("Failed", _, _) => "Failed",
                        ("AwaitingSpeakerOverride", "Transcribing", _) => "AwaitingSpeakerOverride",
                        ("Processing" or "Completed", "Transcribing" or "AwaitingSpeakerOverride", _) => "Processing",
                        ("Completed", "Processing", _) => "Completed",
                        ("Completed", "Summarizing", "1") => "Summarized",
                        _ => null
                    };
                    if (Stage is not null) Signals.Add(new CoachCallWorkflowSignal(Reader.GetGuid(0), Reader.GetGuid(1), Reader.GetString(2), Stage));
                }
        }
        if (Missing.Count > 0)
        {
            var Endpoint = await Sender.GetSendEndpoint(new Uri("queue:personal-agent-coach-call-summaries"));
            foreach (var Call in Missing) await Endpoint.Send(Call, Token);
            Logger.LogInformation("Queued executive summaries for {Count} historical coach calls", Missing.Count);
        }
        if (Workflows.Count > 0)
        {
            var Endpoint = await Sender.GetSendEndpoint(new Uri("queue:personal-agent-coach-call-workflows"));
            foreach (var Workflow in Workflows) await Endpoint.Send(Workflow, Token);
            Logger.LogInformation("Reconciled {Count} coach calls created before workflow migration", Workflows.Count);
        }
        if (Signals.Count > 0)
        {
            var Endpoint = await Sender.GetSendEndpoint(new Uri("queue:personal-agent-coach-call-workflows"));
            foreach (var Signal in Signals) await Endpoint.Send(Signal, Token);
            Logger.LogInformation("Reconciled {Count} coach call workflow transitions", Signals.Count);
        }
    }
}
