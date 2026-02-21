using NodaTime;

namespace SimpleOfficeScheduler.Models;

public class Event
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OwnerUserId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public LocalDateTime StartTime { get; set; }
    public LocalDateTime EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public int Capacity { get; set; } = 1;
    public string TimeZoneId { get; set; } = "America/New_York";

    public RecurrencePattern? Recurrence { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public ICollection<EventOccurrence> Occurrences { get; set; } = new List<EventOccurrence>();
}
