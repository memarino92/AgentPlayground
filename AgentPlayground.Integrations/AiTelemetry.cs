using System.Diagnostics;

namespace AgentPlayground.Integrations;

public static class AiTelemetry
{
    public const string SourceName = "AgentPlayground.AI";
    private static readonly ActivitySource Source = new(SourceName);

    public static Activity? Start(string Name, string Kind, string? Model = null)
    {
        var activity = Source.StartActivity(Name);
        activity?.SetTag("openinference.span.kind", Kind);
        if (Model is not null) activity?.SetTag(Kind == "EMBEDDING" ? "embedding.model_name" : "llm.model_name", Model);
        return activity;
    }

    public static async Task<T> RunAsync<T>(string Name, string Kind, Func<Task<T>> Action, string? Model = null)
    {
        using var activity = Start(Name, Kind, Model);
        try { return await Action(); }
        catch (OperationCanceledException)
        {
            activity?.SetTag("error.type", "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetTag("error.type", exception.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }

    public static void SetUsage(Activity? Activity, long? Input, long? Output, long? Total)
    {
        if (Input.HasValue) Activity?.SetTag("llm.token_count.prompt", Input.Value);
        if (Output.HasValue) Activity?.SetTag("llm.token_count.completion", Output.Value);
        if (Total.HasValue) Activity?.SetTag("llm.token_count.total", Total.Value);
    }
}
