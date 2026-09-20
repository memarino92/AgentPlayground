using PersonalAgent.Services;

namespace PersonalAgent.Development;

// A wiring fixture, not a simulation of Jev accuracy. Only exact known examples select a tool.
internal sealed class SyntheticToolDecisionClient : IToolDecisionClient
{
    public Task<ToolChoiceResult> ChooseAsync(ToolChoiceRequest Request, JevRoutingSnapshot Snapshot, CancellationToken Token)
    {
        Token.ThrowIfCancellationRequested();
        var Choice = Request.Message.Trim().ToLowerInvariant() switch
        {
            "what time is it?" => "get_current_date_time",
            "search my work journal" => "search_work_journal",
            "search my coaching notes" => "search_coach_checkins",
            _ => "main_chat"
        };
        return Task.FromResult(new ToolChoiceResult(Request.Choices.ContainsKey(Choice) ? Choice : "main_chat", 1, 1));
    }
}

// Synthetic environments never need a credential or send text to TypeSafe, even if a key is saved.
internal sealed class SyntheticRoutingSettings(JevRoutingRuntime Runtime) : IJevRoutingSettings
{
    public JevRoutingSnapshot Current => new(Runtime.Current.Settings with { AllowUserContent = true }, "synthetic-only");
}
