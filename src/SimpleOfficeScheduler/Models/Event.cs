namespace SimpleOfficeScheduler.Models;

public class Event
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OwnerUserId { get; set; }
    public AppUser Owner { get; set; } = null!;

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public int Capacity { get; set; } = 1;

    public RecurrencePattern? Recurrence { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<EventOccurrence> Occurrences { get; set; } = new List<EventOccurrence>();
}
