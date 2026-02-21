using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Recurrence;

public class RecurrenceExpander
{
    public List<(DateTime Start, DateTime End)> Expand(Event evt, DateTime horizonEnd)
    {
        var occurrences = new List<(DateTime Start, DateTime End)>();
        var duration = evt.EndTime - evt.StartTime;

        if (evt.Recurrence is null)
        {
            // Single event
            occurrences.Add((evt.StartTime, evt.EndTime));
            return occurrences;
        }

        var pattern = evt.Recurrence;
        var current = evt.StartTime;
        int count = 0;

        while (current <= horizonEnd)
        {
            if (pattern.RecurrenceEndDate.HasValue && current > pattern.RecurrenceEndDate.Value)
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

    private static bool ShouldIncludeDate(DateTime date, RecurrencePattern pattern)
    {
        if (pattern.Type == RecurrenceType.Weekly || pattern.Type == RecurrenceType.BiWeekly)
        {
            if (pattern.DaysOfWeek.Count > 0)
                return pattern.DaysOfWeek.Contains(date.DayOfWeek);
        }

        return true;
    }

    private static DateTime AdvanceByPattern(DateTime current, RecurrencePattern pattern)
    {
        return pattern.Type switch
        {
            RecurrenceType.Daily => current.AddDays(pattern.Interval),
            RecurrenceType.Weekly => AdvanceWeekly(current, pattern),
            RecurrenceType.BiWeekly => AdvanceBiWeekly(current, pattern),
            RecurrenceType.Monthly => current.AddMonths(pattern.Interval),
            _ => current.AddDays(1)
        };
    }

    private static DateTime AdvanceWeekly(DateTime current, RecurrencePattern pattern)
    {
        if (pattern.DaysOfWeek.Count <= 1)
            return current.AddDays(7 * pattern.Interval);

        // Find next day in the list
        var sorted = pattern.DaysOfWeek.OrderBy(d => ((int)d - (int)current.DayOfWeek + 7) % 7).ToList();
        var nextDay = sorted.FirstOrDefault(d => d > current.DayOfWeek);

        if (nextDay != default || sorted.Any(d => d > current.DayOfWeek))
        {
            var target = sorted.First(d => d > current.DayOfWeek);
            int daysUntil = ((int)target - (int)current.DayOfWeek + 7) % 7;
            return current.AddDays(daysUntil);
        }

        // Wrap to first day of next week cycle
        var firstDay = pattern.DaysOfWeek.Min();
        int daysToFirstDay = ((int)firstDay - (int)current.DayOfWeek + 7) % 7;
        if (daysToFirstDay == 0) daysToFirstDay = 7;
        return current.AddDays(daysToFirstDay + 7 * (pattern.Interval - 1));
    }

    private static DateTime AdvanceBiWeekly(DateTime current, RecurrencePattern pattern)
    {
        if (pattern.DaysOfWeek.Count <= 1)
            return current.AddDays(14);

        // Same logic as weekly but with 2-week interval
        var sorted = pattern.DaysOfWeek.OrderBy(d => d).ToList();
        var nextDay = sorted.FirstOrDefault(d => d > current.DayOfWeek);

        if (sorted.Any(d => d > current.DayOfWeek))
        {
            var target = sorted.First(d => d > current.DayOfWeek);
            int daysUntil = ((int)target - (int)current.DayOfWeek + 7) % 7;
            return current.AddDays(daysUntil);
        }

        var firstDay = pattern.DaysOfWeek.Min();
        int daysToFirstDay = ((int)firstDay - (int)current.DayOfWeek + 7) % 7;
        if (daysToFirstDay == 0) daysToFirstDay = 7;
        return current.AddDays(daysToFirstDay + 7); // Skip a week for bi-weekly
    }
}
