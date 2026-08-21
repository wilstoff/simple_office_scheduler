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
    public EventType EventType { get; set; } = EventType.OfficeHours;

    public RecurrencePattern? Recurrence { get; set; }

    /// <summary>
    /// Graph id of the recurring series backing this event. Workshops only; office hours and tech
    /// meetings create a standalone Graph event per occurrence instead.
    /// </summary>
    public string? GraphSeriesId { get; set; }

    /// <summary>
    /// How far the Graph series currently extends. Rolled forward before it lapses rather than
    /// creating a new series, so attendees keep one invite and the per-instance signup exceptions
    /// stay attached.
    /// </summary>
    public LocalDate? GraphSeriesWindowEnd { get; set; }

    public Instant CreatedAt { get; set; }
    public Instant UpdatedAt { get; set; }

    public ICollection<EventOwner> CoOwners { get; set; } = new List<EventOwner>();
    public ICollection<EventOccurrence> Occurrences { get; set; } = new List<EventOccurrence>();
    public ICollection<EventReminderDefinition> ReminderDefinitions { get; set; } = new List<EventReminderDefinition>();
}
