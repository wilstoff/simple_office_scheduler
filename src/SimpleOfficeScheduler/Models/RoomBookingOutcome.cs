namespace SimpleOfficeScheduler.Models;

/// <summary>
/// What a room mailbox did with a booking. Read back from the resource attendee's response rather
/// than assumed at booking time, because the room replies asynchronously.
/// </summary>
public class RoomBookingOutcome
{
    public RoomBookingStatus Status { get; set; }
    public string? Error { get; set; }
}
