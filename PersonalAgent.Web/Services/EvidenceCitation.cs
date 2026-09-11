using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace PersonalAgent.Web.Services;

internal sealed record EvidenceCitation(Guid UploadId, string ProfileId, int? StartMs)
{
    private static readonly Regex Links = new(@"(?<![\w/:])(?:/evidence/|https?://evidence/)[a-fA-F0-9-]{36}(?:\?[^\s)<>""']+)?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Destination = new(@"^(?:/evidence/|https?://evidence/)(?<id>[a-fA-F0-9-]{36})(?:\?(?<query>[^#\s]*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public string Url => $"/evidence/{UploadId}?profileId={Uri.EscapeDataString(ProfileId)}" + (StartMs is int Start ? $"&startMs={Start}" : string.Empty);

    public static EvidenceCitation? FromUrl(string? Url, string? DefaultProfileId = null)
    {
        var Match = Destination.Match(Url ?? string.Empty);
        if (!Match.Success || !Guid.TryParse(Match.Groups["id"].Value, out var Id)) return null;
        var Query = QueryHelpers.ParseQuery(Match.Groups["query"].Value.Replace("&amp;", "&", StringComparison.Ordinal));
        var Profile = Query.TryGetValue("profileId", out var ProfileValue) ? ProfileValue.ToString() : DefaultProfileId;
        if (string.IsNullOrWhiteSpace(Profile)) return null;
        int? Start = Query.TryGetValue("startMs", out var Timing) && int.TryParse(Timing, out var Value) && Value >= 0 ? Value : null;
        return new(Id, Profile, Start);
    }

    public static IReadOnlyList<EvidenceCitation> Parse(string Content, string? DefaultProfileId = null) =>
        Links.Matches(Content).Select(Match => FromUrl(Match.Value, DefaultProfileId)).OfType<EvidenceCitation>().Distinct().ToArray();
}
