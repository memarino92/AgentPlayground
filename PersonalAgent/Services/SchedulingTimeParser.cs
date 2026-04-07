using System.Globalization;

namespace PersonalAgent.Services;

internal static class SchedulingTimeParser
{
    public static DateTimeOffset ResolveExecuteAtUtc(string? delay, DateTimeOffset? executeAt, string? when, string? timeZoneId, DateTimeOffset nowUtc)
    {
        if (executeAt is not null) return executeAt.Value.ToUniversalTime();

        if (!string.IsNullOrWhiteSpace(delay))
        {
            if (!TryParseDuration(delay.Trim(), out var duration))
                throw new InvalidOperationException($"Invalid delay '{delay}'. Use ISO-8601 duration (e.g. PT5M) or 'hh:mm:ss'.");
            return nowUtc.Add(duration);
        }

        if (!string.IsNullOrWhiteSpace(when))
            return ParseNaturalTime(when.Trim(), timeZoneId, nowUtc);

        throw new InvalidOperationException("Provide one of delay, executeAt, or when.");
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (value.StartsWith('P'))
        {
            try
            {
                duration = System.Xml.XmlConvert.ToTimeSpan(value);
                return true;
            }
            catch
            {
                duration = default;
                return false;
            }
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration);
    }

    private static DateTimeOffset ParseNaturalTime(string when, string? timeZoneId, DateTimeOffset nowUtc)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        if (string.Equals(when, "tonight", StringComparison.OrdinalIgnoreCase))
        {
            var localTonight = new DateTime(localNow.Year, localNow.Month, localNow.Day, 19, 0, 0, DateTimeKind.Unspecified);
            if (localTonight <= localNow.DateTime) localTonight = localTonight.AddDays(1);
            var utcDateTime = TimeZoneInfo.ConvertTimeToUtc(localTonight, timeZone);
            return new DateTimeOffset(utcDateTime, TimeSpan.Zero);
        }

        if (!DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLocal))
            throw new InvalidOperationException($"Invalid natural time '{when}'.");

        var local = new DateTime(localNow.Year, localNow.Month, localNow.Day, parsedLocal.Hour, parsedLocal.Minute, parsedLocal.Second, DateTimeKind.Unspecified);
        if (local <= localNow.DateTime) local = local.AddDays(1);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }
}
