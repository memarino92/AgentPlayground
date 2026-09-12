using System.Globalization;

namespace PersonalAgent.Services;

internal static class CoachRecordingDate
{
    // Filenames do not establish a timezone. Do not treat upload time as call time.
    internal static DateTime? Parse(string FileName)
        => FileName.Length > 19 && FileName[19] == '.'
           && DateTime.TryParseExact(FileName[..19], "yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture,
               DateTimeStyles.None, out var Date) ? Date : null;
}
