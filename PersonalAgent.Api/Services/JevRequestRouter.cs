using System.Diagnostics;
using System.Text.RegularExpressions;
using PersonalAgent.Integrations;
using Microsoft.Extensions.AI;
using PersonalAgent.Api.Models;

namespace PersonalAgent.Api.Services;

internal sealed record PreChatRoute(string? Response = null, string? SuggestedTool = null, string Reason = "main_chat");

internal interface IAgentRequestRouter
{
    Task<PreChatRoute> RouteAsync(string Message, IReadOnlyList<BoundAgentTool> Tools, CancellationToken Token);
}

internal sealed partial class JevRequestRouter(IJevRoutingSettings Settings, IToolDecisionClient Decisions,
    ILogger<JevRequestRouter>? Logger = null) : IAgentRequestRouter
{
    public async Task<PreChatRoute> RouteAsync(string Message, IReadOnlyList<BoundAgentTool> Tools, CancellationToken Token)
    {
        var Snapshot = Settings.Current;
        var Started = Stopwatch.GetTimestamp();
        var CandidateCount = 0;
        string? SelectedTool = null;
        double? Probability = null, Confidence = null;
        string? Gate = null;
        using var Span = AiTelemetry.Start("agent.route", "CHAIN");
        Span?.SetTag("routing.mode", Snapshot.Settings.Mode.ToString());
        Span?.SetTag("decision.policy", JevToolRoutingCatalog.Policy);
        if (!Snapshot.CanCall) return Finish(new(Reason: "disabled"));
        var Candidates = Tools.Select(Tool => (Tool, Description: JevToolRoutingCatalog.Description(Tool)))
            .Where(Candidate => Candidate.Description is not null).ToArray();
        CandidateCount = Candidates.Length;
        if (Candidates.Length is 0 or > 254) return Finish(new(Reason: "candidate_limit"));
        var Choices = Candidates.ToDictionary(Candidate => Candidate.Tool.Function.Name, Candidate => Candidate.Description!, StringComparer.Ordinal);
        Choices.Add("main_chat", "Conversation, multiple tasks, negation, quoted commands, missing context, unsupported requests or uncertainty.");
        var Decision = await Decisions.ChooseAsync(new(Message, Choices), Snapshot, Token);
        Token.ThrowIfCancellationRequested();
        if (Decision.Choice is null) return Finish(new(Reason: Decision.Reason));
        if (Decision.Choice == "main_chat") return Finish(new());
        var Selected = Candidates.SingleOrDefault(Candidate => Candidate.Tool.Function.Name == Decision.Choice).Tool;
        if (Selected is null || !double.IsFinite(Decision.Probability) || !double.IsFinite(Decision.Confidence)
            || Decision.Probability is < 0 or > 1 || Decision.Confidence is < 0 or > 1)
        {
            Gate = "invalid_selection";
            return Finish(new(Reason: "abstained"));
        }
        SelectedTool = Selected.Function.Name;
        Probability = Decision.Probability;
        Confidence = Decision.Confidence;
        var LowProbability = Probability < Snapshot.Settings.MinimumProbability;
        var LowConfidence = Confidence < Snapshot.Settings.MinimumConfidence;
        if (LowProbability || LowConfidence)
        {
            Gate = LowProbability && LowConfidence ? "probability_and_confidence" : LowProbability ? "probability" : "confidence";
            return Finish(new(Reason: "abstained"));
        }
        Span?.SetTag("tool.name", Selected.Function.Name);
        if (Snapshot.Settings.Mode == JevRoutingMode.Shadow) return Finish(new(Reason: "shadow"));
        var Arguments = new AIFunctionArguments();
        var CanExecute = Snapshot.Settings.Mode switch
        {
            JevRoutingMode.DirectReadOnly => TryCompile(Message, Selected, out Arguments),
            JevRoutingMode.DirectTools => JevToolArgumentCompiler.TryCompile(Message, Selected, out Arguments),
            _ => false
        };
        if (CanExecute)
        {
            // Invoke the already bound wrapper: current authorization, tool tracing and error capture remain authoritative.
            // Do not fall back after execution starts; that could cause the LLM to repeat an operation.
            try
            {
                var Result = await Selected.Function.InvokeAsync(Arguments, Token);
                return Finish(new(Response: JevToolResultFormatter.Format(Result), Reason: "direct"));
            }
            catch (Exception Exception)
            {
                Span?.SetTag("routing.outcome", "execution_failed");
                Span?.SetTag("error.type", Exception is OperationCanceledException ? "cancelled" : Exception.GetType().FullName);
                if (Exception is not OperationCanceledException) Span?.SetStatus(ActivityStatusCode.Error);
                Finish(new(Reason: "execution_failed"));
                throw;
            }
        }
        return Finish(new(SuggestedTool: Selected.Function.Name, Reason: "suggest"));

        PreChatRoute Finish(PreChatRoute Result)
        {
            Span?.SetTag("routing.outcome", Result.Reason);
            Logger?.LogInformation(new EventId(2604),
                "Jev routing: mode={RoutingMode}; outcome={RoutingOutcome}; tool={SelectedTool}; candidates={CandidateCount}; elapsedMs={ElapsedMilliseconds}; probability={Probability}; confidence={Confidence}; minimumProbability={MinimumProbability}; minimumConfidence={MinimumConfidence}; gate={Gate}",
                Snapshot.Settings.Mode, Result.Reason, SelectedTool, CandidateCount, Stopwatch.GetElapsedTime(Started).TotalMilliseconds,
                Probability, Confidence, Snapshot.Settings.MinimumProbability, Snapshot.Settings.MinimumConfidence, Gate);
            return Result;
        }
    }

    internal static bool TryCompile(string Message, BoundAgentTool Tool, out AIFunctionArguments Arguments)
    {
        Arguments = new();
        // First enrolled adapter: a complete, standalone clock request with no inferred arguments.
        // Other tools (especially mutations) remain in the existing chat loop until independently evaluated.
        if (Tool.Descriptor.Key != AgentToolKeys.GetCurrentDateTime || Tool.Descriptor.HasSideEffects || !ClockRequest().IsMatch(Message.Trim())) return false;
        Arguments["timeZoneId"] = null;
        return true;
    }

    [GeneratedRegex(@"\A(?:what(?:'s| is) (?:the )?(?:current )?(?:time|date|date and time)|what time is it(?: now)?|tell me (?:the )?(?:current )?(?:time|date|date and time))(?: please)?[?.!]?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockRequest();
}
