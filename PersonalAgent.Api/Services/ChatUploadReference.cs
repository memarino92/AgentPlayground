using System.Text.RegularExpressions;

namespace PersonalAgent.Api.Services;

internal sealed partial record ChatUploadReference(string Name, Guid Id)
{
    public static ChatUploadReference? Parse(string Message)
    {
        var Match = AttachmentLine().Match(Message);
        return Match.Success && Guid.TryParseExact(Match.Groups[2].Value, "D", out var Id)
            ? new(Match.Groups[1].Value, Id) : null;
    }

    [GeneratedRegex(@"^Attached file ([^\r\n]{1,255}) \(attachment:([0-9a-fA-F-]{36})\)\r?$", RegexOptions.Multiline)]
    private static partial Regex AttachmentLine();
}
