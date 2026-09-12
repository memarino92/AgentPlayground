using System.Diagnostics;
using System.Diagnostics.Metrics;
using OpenTelemetry;

namespace AgentPlayground.Integrations;

public sealed class AiMetricsProcessor : BaseProcessor<Activity>
{
    public const string MeterName = "AgentPlayground.AI";
    private readonly Histogram<double> Duration;
    private readonly Counter<long> Tokens;

    public AiMetricsProcessor(IMeterFactory Factory)
    {
        var meter = Factory.Create(MeterName);
        Duration = meter.CreateHistogram<double>("agent.operation.duration", "s", "AI operation duration by kind and outcome");
        Tokens = meter.CreateCounter<long>("agent.token.usage", "{token}", "Provider-reported input and output tokens");
    }

    public override void OnEnd(Activity Activity)
    {
        if (Activity.Source.Name != AiTelemetry.SourceName) return;
        var tags = new TagList
        {
            { "operation.kind", Activity.GetTagItem("openinference.span.kind") },
            { "outcome", Activity.GetTagItem("error.type") as string == "cancelled" ? "cancelled"
                : Activity.Status == ActivityStatusCode.Error ? "error" : "success" }
        };
        Duration.Record(Activity.Duration.TotalSeconds, tags);
        foreach (var (attribute, direction) in new[] { ("llm.token_count.prompt", "input"), ("llm.token_count.completion", "output") })
            if (Activity.GetTagItem(attribute) is long count)
                Tokens.Add(count, new KeyValuePair<string, object?>("token.direction", direction));
    }
}
