using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Recurrence;

public class RecurrenceExpander
{
    public List<(LocalDateTime Start, LocalDateTime End)> Expand(Event evt, LocalDateTime horizonEnd)
    {
        var occurrences = new List<(LocalDateTime Start, LocalDateTime End)>();
        var duration = Period.Between(evt.StartTime, evt.EndTime);

        if (evt.Recurrence is null)
        {
            // Single event
            occurrences.Add((evt.StartTime, evt.EndTime));
            return occurrences;
        }

        var pattern = evt.Recurrence;
        var current = evt.StartTime;
        int count = 0;

        while (current.CompareTo(horizonEnd) <= 0)
        {
            if (pattern.RecurrenceEndDate.HasValue && current.Date.CompareTo(pattern.RecurrenceEndDate.Value) > 0)
                break;

            if (pattern.MaxOccurrences.HasValue && count >= pattern.MaxOccurrences.Value)
                break;

            if (ShouldIncludeDate(current, pattern))
            {
                occurrences.Add((current, current + duration));
                count++;
            }

            current = AdvanceByPattern(current, pattern);
        }

        return occurrences;
    }

    private static bool ShouldIncludeDate(LocalDateTime date, RecurrencePattern pattern)
    {
        if (pattern.Type == RecurrenceType.Weekly || pattern.Type == RecurrenceType.BiWeekly)
        {
            if (pattern.DaysOfWeek.Count > 0)
                return pattern.DaysOfWeek.Contains(date.DayOfWeek.ToDayOfWeek());
        }

        return true;
    }

    private static LocalDateTime AdvanceByPattern(LocalDateTime current, RecurrencePattern pattern)
    {
        return pattern.Type switch
        {
            RecurrenceType.Daily => current.PlusDays(pattern.Interval),
            RecurrenceType.Weekly => AdvanceWeekly(current, pattern),
            RecurrenceType.BiWeekly => AdvanceBiWeekly(current, pattern),
            RecurrenceType.Monthly => current.PlusMonths(pattern.Interval),
            _ => current.PlusDays(1)
        };
    }

    private static LocalDateTime AdvanceWeekly(LocalDateTime current, RecurrencePattern pattern)
    {
        var currentDow = current.DayOfWeek.ToDayOfWeek();

        if (pattern.DaysOfWeek.Count <= 1)
            return current.PlusDays(7 * pattern.Interval);

        // Find next day in the list
        var sorted = pattern.DaysOfWeek.OrderBy(d => ((int)d - (int)currentDow + 7) % 7).ToList();

        if (sorted.Any(d => d > currentDow))
        {
            var target = sorted.First(d => d > currentDow);
            int daysUntil = ((int)target - (int)currentDow + 7) % 7;
            return current.PlusDays(daysUntil);
        }

        // Wrap to first day of next week cycle
        var firstDay = pattern.DaysOfWeek.Min();
        int daysToFirstDay = ((int)firstDay - (int)currentDow + 7) % 7;
        if (daysToFirstDay == 0) daysToFirstDay = 7;
        return current.PlusDays(daysToFirstDay + 7 * (pattern.Interval - 1));
    }

    private static LocalDateTime AdvanceBiWeekly(LocalDateTime current, RecurrencePattern pattern)
    {
        var currentDow = current.DayOfWeek.ToDayOfWeek();

        if (pattern.DaysOfWeek.Count <= 1)
            return current.PlusDays(14);

        // Same logic as weekly but with 2-week interval
        var sorted = pattern.DaysOfWeek.OrderBy(d => d).ToList();

        if (sorted.Any(d => d > currentDow))
        {
            var target = sorted.First(d => d > currentDow);
            int daysUntil = ((int)target - (int)currentDow + 7) % 7;
            return current.PlusDays(daysUntil);
        }

        var firstDay = pattern.DaysOfWeek.Min();
        int daysToFirstDay = ((int)firstDay - (int)currentDow + 7) % 7;
        if (daysToFirstDay == 0) daysToFirstDay = 7;
        return current.PlusDays(daysToFirstDay + 7); // Skip a week for bi-weekly
    }
}

/// <summary>
/// Extension to convert NodaTime IsoDayOfWeek to System.DayOfWeek.
/// </summary>
internal static class IsoDayOfWeekExtensions
{
    public static DayOfWeek ToDayOfWeek(this IsoDayOfWeek isoDow) => isoDow switch
    {
        IsoDayOfWeek.Monday => DayOfWeek.Monday,
        IsoDayOfWeek.Tuesday => DayOfWeek.Tuesday,
        IsoDayOfWeek.Wednesday => DayOfWeek.Wednesday,
        IsoDayOfWeek.Thursday => DayOfWeek.Thursday,
        IsoDayOfWeek.Friday => DayOfWeek.Friday,
        IsoDayOfWeek.Saturday => DayOfWeek.Saturday,
        IsoDayOfWeek.Sunday => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(isoDow))
    };
}
