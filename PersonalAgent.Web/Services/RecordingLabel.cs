using System.Globalization;

namespace PersonalAgent.Web.Services;

internal static class RecordingLabel
{
    public static string FromFileName(string FileName) => FileName.Length > 19 && FileName[19] == '.'
        && DateTime.TryParseExact(FileName[..19], "yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var Date)
        ? Date.ToString("MMMM d, yyyy 'at' h:mm tt", CultureInfo.CurrentCulture) : FileName;
}
