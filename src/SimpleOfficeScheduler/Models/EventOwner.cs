namespace SimpleOfficeScheduler.Models;

/// <summary>
/// An additional owner of an event beyond Event.OwnerUserId. Co-owners have the same management
/// rights as the creator and are attendees on the event's meeting.
/// </summary>
public class EventOwner
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
}
