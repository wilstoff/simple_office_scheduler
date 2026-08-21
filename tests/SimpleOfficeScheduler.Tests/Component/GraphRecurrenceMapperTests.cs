using Microsoft.Graph.Models;
using NodaTime;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Calendar;
using Event = SimpleOfficeScheduler.Models.Event;
using EventType = SimpleOfficeScheduler.Models.EventType;
using RecurrencePattern = SimpleOfficeScheduler.Models.RecurrencePattern;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// The app expands recurrence itself in RecurrenceExpander, and Graph expands the series from a
/// PatternedRecurrence. These tests pin the translation so the two agree. BiWeekly and
/// FirstDayOfWeek are where they can silently drift.
/// </summary>
public class GraphRecurrenceMapperTests
{
    private static readonly LocalDate SeriesStart = new(2026, 9, 1); // a Tuesday

    private static Event MakeEvent(RecurrenceType type, int interval, params DayOfWeek[] days) => new()
    {
        Title = "Workshop",
        StartTime = SeriesStart.At(new LocalTime(9, 0)),
        EndTime = SeriesStart.At(new LocalTime(10, 0)),
        TimeZoneId = "America/Chicago",
        EventType = EventType.Workshop,
        Recurrence = new RecurrencePattern
        {
            Type = type,
            Interval = interval,
            DaysOfWeek = days.ToList()
        }
    };

    [Fact]
    public void Daily_MapsToDailyPatternWithInterval()
    {
        var evt = MakeEvent(RecurrenceType.Daily, 2);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(RecurrencePatternType.Daily, result.Pattern!.Type);
        Assert.Equal(2, result.Pattern.Interval);
    }

    [Fact]
    public void Weekly_MapsDaysOfWeek()
    {
        var evt = MakeEvent(RecurrenceType.Weekly, 1, DayOfWeek.Tuesday, DayOfWeek.Thursday);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(RecurrencePatternType.Weekly, result.Pattern!.Type);
        Assert.Equal(1, result.Pattern.Interval);
        Assert.Equal(
            new DayOfWeekObject?[] { DayOfWeekObject.Tuesday, DayOfWeekObject.Thursday },
            result.Pattern.DaysOfWeek!);
    }

    [Fact]
    public void Weekly_WithNoDaysSelected_FallsBackToTheStartDay()
    {
        // RecurrenceExpander advances by whole weeks from the start date when DaysOfWeek is empty,
        // so the Graph series has to name that day explicitly or Graph rejects the pattern.
        var evt = MakeEvent(RecurrenceType.Weekly, 1);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(new DayOfWeekObject?[] { DayOfWeekObject.Tuesday }, result.Pattern!.DaysOfWeek!);
    }

    [Fact]
    public void Weekly_AnchorsFirstDayOfWeekToTheSeriesStart()
    {
        // Graph applies the interval from FirstDayOfWeek. RecurrenceExpander counts cycles from the
        // event's own start day, so anchoring here is what keeps intervals above 1 aligned.
        var evt = MakeEvent(RecurrenceType.Weekly, 3, DayOfWeek.Tuesday);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(DayOfWeekObject.Tuesday, result.Pattern!.FirstDayOfWeek);
    }

    [Fact]
    public void BiWeekly_IsAlwaysEveryTwoWeeksRegardlessOfInterval()
    {
        // AdvanceBiWeekly in RecurrenceExpander ignores Interval and always skips two weeks.
        // The mapping has to hardcode 2 to match, not multiply.
        var evt = MakeEvent(RecurrenceType.BiWeekly, 3, DayOfWeek.Tuesday);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(RecurrencePatternType.Weekly, result.Pattern!.Type);
        Assert.Equal(2, result.Pattern.Interval);
    }

    [Fact]
    public void Monthly_MapsToAbsoluteMonthlyOnTheStartDay()
    {
        var evt = MakeEvent(RecurrenceType.Monthly, 1);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(RecurrencePatternType.AbsoluteMonthly, result.Pattern!.Type);
        Assert.Equal(1, result.Pattern.DayOfMonth);
        Assert.Equal(1, result.Pattern.Interval);
    }

    [Fact]
    public void NonRecurringEvent_MapsToNull()
    {
        var evt = MakeEvent(RecurrenceType.Daily, 1);
        evt.Recurrence = null;

        Assert.Null(GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1)));
    }

    [Fact]
    public void Range_UsesEndDateTypeAndTheGivenBound()
    {
        var evt = MakeEvent(RecurrenceType.Weekly, 1, DayOfWeek.Tuesday);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(RecurrenceRangeType.EndDate, result!.Range!.Type);
        Assert.Equal(new Microsoft.Kiota.Abstractions.Date(2026, 9, 1), result.Range.StartDate);
        Assert.Equal(new Microsoft.Kiota.Abstractions.Date(2027, 3, 1), result.Range.EndDate);
    }

    [Fact]
    public void Range_ClampsToTheRecurrenceEndDateWhenItIsEarlier()
    {
        var evt = MakeEvent(RecurrenceType.Weekly, 1, DayOfWeek.Tuesday);
        evt.Recurrence!.RecurrenceEndDate = new LocalDate(2026, 11, 15);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(new Microsoft.Kiota.Abstractions.Date(2026, 11, 15), result!.Range!.EndDate);
    }

    [Fact]
    public void Range_KeepsTheWindowBoundWhenTheRecurrenceEndDateIsLater()
    {
        var evt = MakeEvent(RecurrenceType.Weekly, 1, DayOfWeek.Tuesday);
        evt.Recurrence!.RecurrenceEndDate = new LocalDate(2029, 1, 1);

        var result = GraphRecurrenceMapper.Map(evt, new LocalDate(2027, 3, 1));

        Assert.Equal(new Microsoft.Kiota.Abstractions.Date(2027, 3, 1), result!.Range!.EndDate);
    }
}
