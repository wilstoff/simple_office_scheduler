using NodaTime;

namespace SimpleOfficeScheduler.Models;

public class EventOccurrence
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;

    public LocalDateTime StartTime { get; set; }
    public LocalDateTime EndTime { get; set; }
    public bool IsCancelled { get; set; }
    public string? GraphEventId { get; set; }

    public ICollection<EventSignup> Signups { get; set; } = new List<EventSignup>();
}
