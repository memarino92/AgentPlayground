using System.Text.RegularExpressions;

internal static class EvalScorer
{
    private static readonly Regex Links = new(@"\[[^\]]*\]\(\s*(?<url>[^\s)]+)\s*\)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BareLinks = new(@"(?<![\w/:])(?:https?://[^\s)<>""']+|/evidence/[a-fA-F0-9-]{36}\?[^\s)<>""']+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string[] Destinations(string Text) => Links.Matches(Text).Select(m => m.Groups["url"].Value)
        .Concat(BareLinks.Matches(Text).Select(m => m.Value)).Distinct(StringComparer.Ordinal).ToArray();
    private static readonly Regex Start = new(@"(?:\?|&)startMs=(?<ms>\d+)(?:&|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    internal static Dictionary<string, bool> Score(EvalCase Test, string? Answer, TrialTrace Trace)
    {
        var Text = Answer ?? string.Empty;
        var Citations = Destinations(Text);
        var Returned = Trace.Evidence.SelectMany(Destinations).ToHashSet(StringComparer.Ordinal);
        var Calls = Trace.Calls.Where(c => c.Name == "search_coach_checkins").ToArray();
        var Supporting = Citations.Where(c => Test.ExpectedUpload is not null && c.StartsWith($"/evidence/{Test.ExpectedUpload}?profileId={Uri.EscapeDataString(Test.Subject)}", StringComparison.Ordinal)).ToArray();
        return new()
        {
            ["nonemptyAnswer"] = !string.IsNullOrWhiteSpace(Text),
            ["coachingToolUsed"] = Calls.Length > 0,
            ["requiredConcepts"] = Test.RequiredPatterns.All(p => Matches(Text, p)),
            ["forbiddenClaimsAbsent"] = Test.ForbiddenPatterns.All(p => !Matches(Text, p)),
            ["expectedSource"] = Test.NoEvidence ? Citations.Length == 0 : Supporting.Length > 0,
            ["supportingTimestamp"] = Test.ExpectedStartMs is not int Expected || Supporting.Any(c =>
                int.TryParse(Start.Match(c).Groups["ms"].Value, out var Ms) && Math.Abs((long)Ms - Expected) <= Test.TimestampToleranceMs),
            ["citationsReturnedByTool"] = Citations.All(Returned.Contains),
            ["recencyMode"] = Test.ExpectedRecency is null || Calls.Any(c =>
                (Argument(c.Arguments, "recency") ?? "recent") == Test.ExpectedRecency),
            ["filenameScope"] = Test.ExpectedFileName is null || Calls.Any(c =>
                string.Equals(Argument(c.Arguments, "fileName"), Test.ExpectedFileName, StringComparison.OrdinalIgnoreCase))
        };
    }

    private static bool Matches(string Text, string Pattern)
        => Regex.IsMatch(Text, Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string? Argument(IDictionary<string, object?>? Arguments, string Name)
        => Arguments?.TryGetValue(Name, out var Value) == true ? Value?.ToString() : null;
}
