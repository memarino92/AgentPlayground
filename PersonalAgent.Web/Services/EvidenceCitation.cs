using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace PersonalAgent.Web.Services;

internal sealed record EvidenceCitation(Guid UploadId, string ProfileId, int? StartMs)
{
    private static readonly Regex Links = new(@"/evidence/(?<id>[a-fA-F0-9-]{36})\?(?<query>[^\s)<>""']+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<EvidenceCitation> Parse(string Content)
    {
        var Citations = new List<EvidenceCitation>();
        foreach (Match Match in Links.Matches(Content))
        {
            if (!Guid.TryParse(Match.Groups["id"].Value, out var Id)) continue;
            var Query = QueryHelpers.ParseQuery(Match.Groups["query"].Value.Replace("&amp;", "&", StringComparison.Ordinal));
            var Profile = Query.TryGetValue("profileId", out var ProfileValue) ? ProfileValue.ToString() : string.Empty;
            if (string.IsNullOrWhiteSpace(Profile)) continue;
            int? Start = Query.TryGetValue("startMs", out var Timing) && int.TryParse(Timing, out var Value) && Value >= 0 ? Value : null;
            Citations.Add(new(Id, Profile, Start));
        }
        return Citations.Distinct().ToArray();
    }
}
