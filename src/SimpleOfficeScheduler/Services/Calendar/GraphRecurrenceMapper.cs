using Microsoft.Graph.Models;
using NodaTime;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Recurrence;
using AppEvent = SimpleOfficeScheduler.Models.Event;
using GraphRecurrencePattern = Microsoft.Graph.Models.RecurrencePattern;

namespace SimpleOfficeScheduler.Services.Calendar;

/// <summary>
/// Translates the app's RecurrencePattern into the PatternedRecurrence that Graph expands
/// server-side. The app also expands the same pattern itself in RecurrenceExpander, so this has to
/// match that class's behavior rather than the pattern's nominal meaning. Two places where those
/// differ:
///
/// - AdvanceBiWeekly ignores Interval and always skips two weeks, so BiWeekly maps to a fixed
///   interval of 2.
/// - The expander counts week cycles from the event's own start day, so FirstDayOfWeek is anchored
///   to the start day rather than left at Graph's Sunday default.
///
/// MaxOccurrences is not mapped. A numbered range cannot be rolled forward as the room booking
/// window advances, so workshops reject MaxOccurrences at validation instead.
/// </summary>
public static class GraphRecurrenceMapper
{
    public static PatternedRecurrence? Map(AppEvent evt, LocalDate windowEnd)
    {
        if (evt.Recurrence is null) return null;

        var pattern = evt.Recurrence;
        var startDate = evt.StartTime.Date;
        var startDay = ToGraphDay(startDate.DayOfWeek.ToDayOfWeek());

        var days = pattern.DaysOfWeek.Count > 0
            ? pattern.DaysOfWeek.Select(ToGraphDay).ToList()
            : new List<DayOfWeekObject?> { startDay };

        var graphPattern = pattern.Type switch
        {
            RecurrenceType.Daily => new GraphRecurrencePattern
            {
                Type = RecurrencePatternType.Daily,
                Interval = pattern.Interval
            },
            RecurrenceType.Weekly => new GraphRecurrencePattern
            {
                Type = RecurrencePatternType.Weekly,
                Interval = pattern.Interval,
                DaysOfWeek = days,
                FirstDayOfWeek = startDay
            },
            RecurrenceType.BiWeekly => new GraphRecurrencePattern
            {
                Type = RecurrencePatternType.Weekly,
                Interval = 2,
                DaysOfWeek = days,
                FirstDayOfWeek = startDay
            },
            RecurrenceType.Monthly => new GraphRecurrencePattern
            {
                Type = RecurrencePatternType.AbsoluteMonthly,
                Interval = pattern.Interval,
                DayOfMonth = startDate.Day
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(evt), $"Unsupported recurrence type {pattern.Type}.")
        };

        // The window bound keeps the series inside the room mailbox booking window. A recurrence
        // end date earlier than that wins; a later one gets clamped and rolled forward later.
        var rangeEnd = pattern.RecurrenceEndDate.HasValue
            && pattern.RecurrenceEndDate.Value.CompareTo(windowEnd) < 0
                ? pattern.RecurrenceEndDate.Value
                : windowEnd;

        return new PatternedRecurrence
        {
            Pattern = graphPattern,
            Range = new RecurrenceRange
            {
                Type = RecurrenceRangeType.EndDate,
                StartDate = ToKiotaDate(startDate),
                EndDate = ToKiotaDate(rangeEnd)
            }
        };
    }

    public static Microsoft.Kiota.Abstractions.Date ToKiotaDate(LocalDate date) =>
        new(date.Year, date.Month, date.Day);

    private static DayOfWeekObject? ToGraphDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => DayOfWeekObject.Sunday,
        DayOfWeek.Monday => DayOfWeekObject.Monday,
        DayOfWeek.Tuesday => DayOfWeekObject.Tuesday,
        DayOfWeek.Wednesday => DayOfWeekObject.Wednesday,
        DayOfWeek.Thursday => DayOfWeekObject.Thursday,
        DayOfWeek.Friday => DayOfWeekObject.Friday,
        DayOfWeek.Saturday => DayOfWeekObject.Saturday,
        _ => throw new ArgumentOutOfRangeException(nameof(day))
    };
}
