using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PersonalAgent.Integrations;

public static class AutomationTelemetry
{
    public const string SourceName = "PersonalAgent.Automations";
    public static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("automation.runs");
    private static readonly Histogram<double> Steps = Meter.CreateHistogram<double>("automation.step.duration", "s");
    public static void Finished(string Status) => Runs.Add(1, new KeyValuePair<string, object?>("automation.status", Status));
    public static void StepFinished(string Action, string Status, double Seconds) => Steps.Record(Seconds,
        new KeyValuePair<string, object?>("automation.action", Action), new KeyValuePair<string, object?>("automation.status", Status));
    public static Activity? Start(string Name, Guid RunId, int? Step = null)
    {
        var Activity = Source.StartActivity(Name);
        Activity?.SetTag("automation.run_id", RunId.ToString());
        if (Step is not null) Activity?.SetTag("automation.step_index", Step);
        return Activity;
    }
}
