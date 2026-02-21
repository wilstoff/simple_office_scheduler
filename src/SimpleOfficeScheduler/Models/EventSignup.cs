using NodaTime;

namespace SimpleOfficeScheduler.Models;

public class EventSignup
{
    public int Id { get; set; }
    public int EventOccurrenceId { get; set; }
    public EventOccurrence Occurrence { get; set; } = null!;
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Instant SignedUpAt { get; set; }
}
