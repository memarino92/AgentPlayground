using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace PersonalAgent.Tests.Services;

public sealed class CoachEvaluationScorerTests
{
    private const string Id = "11111111-1111-1111-1111-111111111111";
    private const string Url = "/evidence/11111111-1111-1111-1111-111111111111?profileId=athlete&startMs=1000";
    private static EvalCase Case => new("latest", "athlete", "Latest carry feedback?", Id, 1000, 0,
        ["breath", "pause"], ["foot speed"], "latest", null, false);

    [Fact]
    public void PassingAnswer_RequiresConceptsSourceTimingAndToolMode()
    {
        var Trace = Evidence();
        var Checks = EvalScorer.Score(Case, $"Breathe on entry; remove the pause. [Source]({Url})", Trace);
        Checks.Values.Should().OnlyContain(Value => Value);
    }

    [Theory]
    [InlineData("startMs=1000", "startMs=2000", "supportingTimestamp")]
    [InlineData("profileId=athlete", "profileId=other", "expectedSource")]
    public void PlausibleButWrongCitation_Fails(string From, string To, string Check)
    {
        var Checks = EvalScorer.Score(Case, $"Breathe on entry; remove the pause. [Source]({Url.Replace(From, To)})", Evidence());
        Checks[Check].Should().BeFalse();
        Checks["citationsReturnedByTool"].Should().BeFalse();
    }

    [Fact]
    public void UnsupportedClaimOrAbsentTool_FailsIndependently()
    {
        EvalScorer.Score(Case, $"Improve foot speed. [Source]({Url})", Evidence())["forbiddenClaimsAbsent"].Should().BeFalse();
        EvalScorer.Score(Case, $"Breathe; remove the pause. [Source]({Url})", new())["coachingToolUsed"].Should().BeFalse();
    }

    [Fact]
    public void EmptyScope_CannotPassWithInventedEvidence()
    {
        var Missing = Case with { NoEvidence = true, ExpectedUpload = null, ExpectedStartMs = null, RequiredPatterns = ["no evidence"], ForbiddenPatterns = [], ExpectedRecency = null };
        EvalScorer.Score(Missing, "No evidence was found.", Evidence()).Values.Should().OnlyContain(Value => Value);
        EvalScorer.Score(Missing, $"No evidence was found. [Source]({Url})", Evidence())["expectedSource"].Should().BeFalse();
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("spaced")]
    public void AppSupportedCitationSyntax_IsNotScoredAsMissing(string Style)
    {
        var Citation = Style == "bare" ? Url : $"[Source]( {Url} )";
        EvalScorer.Score(Case, $"Breathe on entry; remove the pause. {Citation}", Evidence())
            .Values.Should().OnlyContain(Value => Value);
    }

    [Fact]
    public void MissingScope_NegatedAttributionIsNotAnAffirmativeClaim()
    {
        var Missing = Case with { NoEvidence = true, ExpectedUpload = null, ExpectedStartMs = null,
            RequiredPatterns = ["no indexed"], ForbiddenPatterns = [@"(?m)^\s*(?:your coach|they|he|she)\s+(?:said|advised|recommended|told)\b"], ExpectedRecency = null };
        EvalScorer.Score(Missing, "No indexed excerpts were found. I cannot report what your coach said.", Evidence())
            .Values.Should().OnlyContain(Value => Value);
        EvalScorer.Score(Missing, "No indexed excerpts.\nYour coach said to breathe.", Evidence())["forbiddenClaimsAbsent"].Should().BeFalse();
    }

    [Fact]
    public async Task EmptyRescore_CannotReportSuccess()
    {
        var Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coach-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        try
        {
            var CasePath = System.IO.Path.Combine(Root, "cases.json");
            await File.WriteAllTextAsync(CasePath, System.Text.Json.JsonSerializer.Serialize(new EvalDataset(2, "empty", [Case])));
            var Action = () => EvalRescore.RunAsync(Root, CasePath);
            await Action.Should().ThrowAsync<InvalidOperationException>().WithMessage("No saved evaluation trials*");
        }
        finally { Directory.Delete(Root, recursive: true); }
    }

    private static TrialTrace Evidence()
    {
        var Trace = new TrialTrace();
        Trace.Evidence.Add($"[Call evidence]({Url})");
        Trace.Calls.Add(new FunctionCallContent("call", "search_coach_checkins", new Dictionary<string, object?> { ["recency"] = "latest" }));
        return Trace;
    }
}
