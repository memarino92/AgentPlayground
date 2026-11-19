using System.Text.RegularExpressions;
using System.Web;

namespace PersonalAgent.Presentation;

public sealed record EvidenceCitation(Guid UploadId, string ProfileId, int? StartMs)
{
    private static readonly Regex Links = new(@"(?<![\w/:])(?:/evidence/|https?://evidence/)[a-fA-F0-9-]{36}(?:\?[^\s)<>""']+)?", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Destination = new(@"^(?:/evidence/|https?://evidence/)(?<id>[a-fA-F0-9-]{36})(?:\?(?<query>[^#\s]*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public string Url => $"/evidence/{UploadId}?profileId={Uri.EscapeDataString(ProfileId)}" + (StartMs is int Start ? $"&startMs={Start}" : string.Empty);

    public static EvidenceCitation? FromUrl(string? Url, string? DefaultProfileId = null)
    {
        var Match = Destination.Match(Url ?? string.Empty);
        if (!Match.Success || !Guid.TryParse(Match.Groups["id"].Value, out var Id)) return null;
        var Query = HttpUtility.ParseQueryString(Match.Groups["query"].Value.Replace("&amp;", "&", StringComparison.Ordinal));
        var Profile = Query["profileId"] ?? DefaultProfileId;
        if (string.IsNullOrWhiteSpace(Profile)) return null;
        int? Start = int.TryParse(Query["startMs"], out var Value) && Value >= 0 ? Value : null;
        return new(Id, Profile, Start);
    }

    public static IReadOnlyList<EvidenceCitation> Parse(string Content, string? DefaultProfileId = null) =>
        Links.Matches(Content).Select(Match => FromUrl(Match.Value, DefaultProfileId)).OfType<EvidenceCitation>().Distinct().ToArray();
}
