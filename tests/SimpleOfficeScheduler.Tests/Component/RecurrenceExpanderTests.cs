using NodaTime;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Recurrence;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Expand() walks forward from the start date until it passes the horizon. Any pattern that fails to
/// advance the cursor spins forever, pinning a CPU and growing the result list until the process
/// dies, so a bad interval has to be survivable rather than merely unlikely.
/// </summary>
public class RecurrenceExpanderTests
{
    private static readonly LocalDateTime Start = new(2026, 8, 27, 12, 0); // a Thursday
    private static readonly LocalDateTime Horizon = new(2027, 2, 27, 23, 59);

    private static Event MakeEvent(RecurrenceType type, int interval, params DayOfWeek[] days) => new()
    {
        Title = "Office Hours",
        StartTime = Start,
        EndTime = Start.PlusHours(1),
        TimeZoneId = "America/Chicago",
        Recurrence = new RecurrencePattern
        {
            Type = type,
            Interval = interval,
            DaysOfWeek = days.ToList()
        }
    };

    [Theory]
    [InlineData(RecurrenceType.Daily)]
    [InlineData(RecurrenceType.Weekly)]
    [InlineData(RecurrenceType.BiWeekly)]
    [InlineData(RecurrenceType.Monthly)]
    public void ZeroInterval_Terminates_AndIsTreatedAsOne(RecurrenceType type)
    {
        // An empty "Every" box in the form binds 0, which made the cursor stand still.
        var zero = new RecurrenceExpander().Expand(MakeEvent(type, 0, DayOfWeek.Thursday), Horizon);
        var one = new RecurrenceExpander().Expand(MakeEvent(type, 1, DayOfWeek.Thursday), Horizon);

        Assert.NotEmpty(zero);
        Assert.Equal(one.Count, zero.Count);
        Assert.Equal(one.Select(o => o.Start), zero.Select(o => o.Start));
    }

    [Theory]
    [InlineData(RecurrenceType.Daily)]
    [InlineData(RecurrenceType.Weekly)]
    [InlineData(RecurrenceType.BiWeekly)]
    [InlineData(RecurrenceType.Monthly)]
    public void NegativeInterval_Terminates_AndIsTreatedAsOne(RecurrenceType type)
    {
        var negative = new RecurrenceExpander().Expand(MakeEvent(type, -3, DayOfWeek.Thursday), Horizon);
        var one = new RecurrenceExpander().Expand(MakeEvent(type, 1, DayOfWeek.Thursday), Horizon);

        Assert.NotEmpty(negative);
        Assert.Equal(one.Select(o => o.Start), negative.Select(o => o.Start));
    }

    [Fact]
    public void ZeroInterval_WithNoDaysSelected_Terminates()
    {
        var result = new RecurrenceExpander().Expand(MakeEvent(RecurrenceType.Weekly, 0), Horizon);

        Assert.NotEmpty(result);
        Assert.All(result, o => Assert.Equal(IsoDayOfWeek.Thursday, o.Start.DayOfWeek));
    }

    [Fact]
    public void WeeklyOnTheStartDay_ProducesOnePerWeekToTheHorizon()
    {
        var result = new RecurrenceExpander().Expand(MakeEvent(RecurrenceType.Weekly, 1, DayOfWeek.Thursday), Horizon);

        Assert.Equal(27, result.Count);
        Assert.Equal(Start, result[0].Start);
        Assert.All(result, o => Assert.Equal(IsoDayOfWeek.Thursday, o.Start.DayOfWeek));
    }

    [Fact]
    public void NoRecurrence_ProducesExactlyOneOccurrence()
    {
        // This is what "it only made one instance" looks like when the recurrence never arrives.
        var evt = MakeEvent(RecurrenceType.Weekly, 1, DayOfWeek.Thursday);
        evt.Recurrence = null;

        var result = new RecurrenceExpander().Expand(evt, Horizon);

        Assert.Single(result);
    }

    [Fact]
    public void Expansion_IsCapped_SoAPathologicalPatternCannotExhaustMemory()
    {
        // Daily over a 100-year horizon would otherwise materialise ~36500 rows per event.
        var result = new RecurrenceExpander()
            .Expand(MakeEvent(RecurrenceType.Daily, 1), Start.PlusYears(100));

        Assert.True(result.Count <= RecurrenceExpander.MaxOccurrencesPerExpansion,
            $"expected at most {RecurrenceExpander.MaxOccurrencesPerExpansion}, got {result.Count}");
    }
}
