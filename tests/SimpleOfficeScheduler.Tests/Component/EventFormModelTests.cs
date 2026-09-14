using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Both create paths — the full page at /events/create and the calendar side panel — build an Event
/// from the same form fields. They used to hold a private copy of that mapping each, and the copies
/// diverged: the panel dropped RoomEmail on create, so a room picked there was silently discarded
/// and the Graph series went out with no resource attendee. Reopening and saving applied the room
/// through SetRoomAsync, which is why it looked like a first-pass problem.
///
/// The mapping now lives here, once, so the two paths cannot diverge again.
/// </summary>
public class EventFormModelTests
{
    private static EventFormModel Filled() => new()
    {
        Title = "Intro to Widgets",
        Description = "A short tour",
        StartTime = new DateTime(2099, 3, 15, 14, 0, 0),
        EndTime = new DateTime(2099, 3, 15, 16, 0, 0),
        Capacity = 12,
        TimeZoneId = "America/Chicago",
        EventType = EventType.Workshop,
        RoomEmail = "training-room@corp.com"
    };

    [Fact]
    public void ToEvent_CarriesTheSelectedRoom()
    {
        var evt = Filled().ToEvent();

        Assert.Equal("training-room@corp.com", evt.RoomEmail);
    }

    [Fact]
    public void ToEvent_LeavesTheRoomNullWhenNoneWasPicked()
    {
        var model = Filled();
        model.RoomEmail = null;

        Assert.Null(model.ToEvent().RoomEmail);
    }

    [Fact]
    public void ToEvent_CopiesTitleDescriptionTimesCapacityTimeZoneAndType()
    {
        var evt = Filled().ToEvent();

        Assert.Equal("Intro to Widgets", evt.Title);
        Assert.Equal("A short tour", evt.Description);
        Assert.Equal(new LocalDateTime(2099, 3, 15, 14, 0), evt.StartTime);
        Assert.Equal(new LocalDateTime(2099, 3, 15, 16, 0), evt.EndTime);
        Assert.Equal(12, evt.Capacity);
        Assert.Equal("America/Chicago", evt.TimeZoneId);
        Assert.Equal(EventType.Workshop, evt.EventType);
    }

    [Fact]
    public void ToEvent_BuildsNoRecurrencePatternWhenNotRecurring()
    {
        var model = Filled();
        model.IsRecurring = false;
        model.RecurrenceType = RecurrenceType.Weekly;
        model.Interval = 2;

        // RecurrenceExpander returns exactly one occurrence when Recurrence is null, so leaking a
        // pattern here would turn a single event into a series without anyone asking.
        Assert.Null(model.ToEvent().Recurrence);
    }

    [Fact]
    public void ToEvent_BuildsTheRecurrencePatternWhenRecurring()
    {
        var model = Filled();
        model.IsRecurring = true;
        model.RecurrenceType = RecurrenceType.Weekly;
        model.Interval = 2;
        model.DaysOfWeek = new List<DayOfWeek> { DayOfWeek.Tuesday, DayOfWeek.Thursday };
        model.RecurrenceEndDate = new DateTime(2099, 6, 15);

        var recurrence = model.ToEvent().Recurrence;

        Assert.NotNull(recurrence);
        Assert.Equal(RecurrenceType.Weekly, recurrence.Type);
        Assert.Equal(2, recurrence.Interval);
        Assert.Equal(new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }, recurrence.DaysOfWeek);
        Assert.Equal(new LocalDate(2099, 6, 15), recurrence.RecurrenceEndDate);
    }

    [Fact]
    public void ToEvent_LeavesTheRecurrenceEndDateNullWhenTheFormHasNone()
    {
        var model = Filled();
        model.IsRecurring = true;
        model.RecurrenceEndDate = null;

        Assert.Null(model.ToEvent().Recurrence!.RecurrenceEndDate);
    }
}
