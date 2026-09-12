using System.Text.RegularExpressions;

namespace PersonalAgent.Services;

internal static class CoachEvidenceLinks
{
    private static readonly Regex Relative = new(@"/evidence/[a-fA-F0-9-]{36}\?profileId=[^\s)<>""']+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Absolute = new(@"(?:https?://[^\s)<>""']+|\\?/evidence\\?/[a-fA-F0-9-]{36}\?profileId=[^\s)<>""']+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    // Repair only destinations that exactly correspond to evidence returned in this turn.
    internal static string Normalize(string Answer, IEnumerable<string> ToolResults)
    {
        var Allowed = ToolResults.SelectMany(Result => Relative.Matches(Result).Select(Match => Match.Value)).ToHashSet(StringComparer.Ordinal);
        return Absolute.Replace(Answer, Match =>
        {
            var Destination = Match.Value.Replace("\\/", "/", StringComparison.Ordinal);
            if (Destination.StartsWith("/evidence/", StringComparison.Ordinal))
                return Allowed.Contains(Destination) ? Destination : Match.Value;
            if (!Uri.TryCreate(Destination, UriKind.Absolute, out var Url)) return Match.Value;
            var Path = Url.AbsolutePath.StartsWith("/evidence/", StringComparison.Ordinal)
                ? Url.PathAndQuery : Url.Host == "evidence" ? "/evidence" + Url.PathAndQuery : string.Empty;
            if (Path.Length == 0 && Url.AbsolutePath == "/")
            {
                var Host = Url.Host.StartsWith("www.", StringComparison.Ordinal) ? Url.Host[4..] : Url.Host;
                if (Host.StartsWith("evidence.", StringComparison.Ordinal) && Guid.TryParseExact(Host[9..], "D", out var Id))
                    Path = $"/evidence/{Id}{Url.Query}";
            }
            return Allowed.Contains(Path) ? Path : Match.Value;
        });
    }
}
