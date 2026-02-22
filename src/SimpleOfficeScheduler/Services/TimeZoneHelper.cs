using NodaTime;
using NodaTime.TimeZones;

namespace SimpleOfficeScheduler.Services;

public static class TimeZoneHelper
{
    private static readonly IDateTimeZoneProvider TzProvider = DateTimeZoneProviders.Tzdb;

    /// <summary>
    /// Validates whether a timezone ID is recognized by NodaTime's TZDB.
    /// </summary>
    public static bool IsValidTimeZoneId(string timeZoneId)
    {
        return TzProvider.GetZoneOrNull(timeZoneId) is not null;
    }

    /// <summary>
    /// Gets a DateTimeZone by IANA ID, falling back to the system local timezone.
    /// </summary>
    public static DateTimeZone GetZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            var zone = TzProvider.GetZoneOrNull(timeZoneId);
            if (zone is not null)
                return zone;
        }

        // Fall back to system local timezone
        return DateTimeZoneProviders.Bcl.GetSystemDefault();
    }

    /// <summary>
    /// Resolves a timezone ID string, falling back to the system default if invalid/null.
    /// Returns the resolved IANA timezone ID.
    /// </summary>
    public static string ResolveTimeZoneId(string? timeZoneId)
    {
        return GetZone(timeZoneId).Id;
    }

    /// <summary>
    /// Converts a wall-clock DateTime in the given timezone to UTC.
    /// NodaTime handles DST gaps and ambiguous times automatically.
    /// </summary>
    public static DateTime WallClockToUtc(DateTime wallClock, string timeZoneId)
    {
        var zone = GetZone(timeZoneId);
        var local = LocalDateTime.FromDateTime(wallClock);
        // InZoneLeniently: gaps → skip forward, ambiguous → use later offset
        var zoned = local.InZoneLeniently(zone);
        return zoned.ToInstant().ToDateTimeUtc();
    }

    /// <summary>
    /// Converts a UTC DateTime to wall-clock time in the given timezone.
    /// </summary>
    public static DateTime UtcToWallClock(DateTime utc, string timeZoneId)
    {
        var zone = GetZone(timeZoneId);
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return instant.InZone(zone).LocalDateTime.ToDateTimeUnspecified();
    }

    // Well-known representative timezones (one per major region/offset)
    private static readonly HashSet<string> CommonIds = new()
    {
        "Pacific/Honolulu",       // UTC-10
        "America/Anchorage",      // UTC-9
        "America/Los_Angeles",    // UTC-8 Pacific
        "America/Denver",         // UTC-7 Mountain
        "America/Chicago",        // UTC-6 Central
        "America/New_York",       // UTC-5 Eastern
        "America/Halifax",        // UTC-4 Atlantic
        "America/Sao_Paulo",      // UTC-3
        "Atlantic/South_Georgia", // UTC-2
        "Atlantic/Azores",        // UTC-1
        "Europe/London",          // UTC+0
        "Europe/Paris",           // UTC+1
        "Europe/Helsinki",        // UTC+2
        "Europe/Moscow",          // UTC+3
        "Asia/Dubai",             // UTC+4
        "Asia/Karachi",           // UTC+5
        "Asia/Kolkata",           // UTC+5:30
        "Asia/Dhaka",             // UTC+6
        "Asia/Bangkok",           // UTC+7
        "Asia/Shanghai",          // UTC+8
        "Asia/Tokyo",             // UTC+9
        "Australia/Sydney",       // UTC+10/+11
        "Pacific/Auckland",       // UTC+12/+13
    };

    /// <summary>
    /// Returns a short list of well-known representative timezones (one per major offset).
    /// </summary>
    public static List<(string Id, string DisplayName)> GetCommonTimeZones()
    {
        return GetTimeZoneList(commonOnly: true);
    }

    /// <summary>
    /// Returns the full list of all IANA timezones for UI display.
    /// </summary>
    public static List<(string Id, string DisplayName)> GetAllTimeZones()
    {
        return GetTimeZoneList(commonOnly: false);
    }

    private static List<(string Id, string DisplayName)> GetTimeZoneList(bool commonOnly)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return TzProvider.Ids
            .Select(id => TzProvider[id])
            .Where(tz => tz.Id.Contains("/")) // Filter out legacy/link IDs like "EST"
            .Where(tz => !commonOnly || CommonIds.Contains(tz.Id))
            .Select(tz =>
            {
                var offset = tz.GetUtcOffset(now);
                var sign = offset >= Offset.Zero ? "+" : "";
                return (
                    Id: tz.Id,
                    DisplayName: $"(UTC{sign}{offset.ToTimeSpan():hh\\:mm}) {tz.Id.Replace("_", " ")}",
                    SortKey: offset.ToTimeSpan()
                );
            })
            .OrderBy(tz => tz.SortKey)
            .ThenBy(tz => tz.Id)
            .Select(tz => (tz.Id, tz.DisplayName))
            .ToList();
    }

    /// <summary>
    /// Gets the IANA timezone ID for the system's local timezone.
    /// </summary>
    public static string GetLocalTimeZoneId()
    {
        return DateTimeZoneProviders.Bcl.GetSystemDefault().Id;
    }
}
