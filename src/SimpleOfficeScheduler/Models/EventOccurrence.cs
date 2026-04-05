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
    public bool IsLightningTalks { get; set; }
    public int? LightningTalksCapacity { get; set; }
    public string? NamePrefix { get; set; }
    public string? NameSuffix { get; set; }

    public string DisplayName
    {
        get
        {
            var prefix = NamePrefix ?? Event?.Title ?? "";
            return string.IsNullOrEmpty(NameSuffix) ? prefix : $"{prefix}: {NameSuffix}";
        }
    }

    public ICollection<EventSignup> Signups { get; set; } = new List<EventSignup>();
    public ICollection<OccurrenceContributor> Contributors { get; set; } = new List<OccurrenceContributor>();
    public ICollection<OccurrenceReminderValue> ReminderValues { get; set; } = new List<OccurrenceReminderValue>();
}
