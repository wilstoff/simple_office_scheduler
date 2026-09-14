using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Occurrences are created in three places: event creation, a schedule adjustment, and the
/// background expansion that rolls the horizon forward. Each one has to seed RoomBookingStatus from
/// whether the event has a room, because RefreshRoomBookingStatusAsync only polls occurrences that
/// are Pending — an occurrence left at None is never checked and never shows the room's answer.
///
/// The background expansion omitted it, so every date added after creation silently stopped being
/// tracked. Building them through one factory is what stops the three sites drifting apart again.
/// </summary>
public class OccurrenceBookingStatusTests
{
    private static readonly LocalDateTime Start = new(2099, 3, 15, 9, 0);
    private static readonly LocalDateTime End = new(2099, 3, 15, 10, 0);

    [Fact]
    public void ForARoomBookedEvent_TheOccurrenceStartsPending()
    {
        var occurrence = EventOccurrence.For(42, Start, End, "room-a@test.local");

        Assert.Equal(42, occurrence.EventId);
        Assert.Equal(Start, occurrence.StartTime);
        Assert.Equal(End, occurrence.EndTime);
        Assert.Equal(RoomBookingStatus.Pending, occurrence.RoomBookingStatus);
    }

    [Fact]
    public void WithNoRoom_TheOccurrenceHasNoBookingStatus()
    {
        Assert.Equal(RoomBookingStatus.None,
            EventOccurrence.For(42, Start, End, null).RoomBookingStatus);
    }

    [Fact]
    public void AnEmptyRoomEmail_CountsAsNoRoom()
    {
        Assert.Equal(RoomBookingStatus.None,
            EventOccurrence.For(42, Start, End, "").RoomBookingStatus);
        Assert.Equal(RoomBookingStatus.None,
            EventOccurrence.For(42, Start, End, "   ").RoomBookingStatus);
    }
}
