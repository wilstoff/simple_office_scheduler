namespace SimpleOfficeScheduler.Models;

public class OccurrenceReminderValue
{
    public int Id { get; set; }
    public int EventOccurrenceId { get; set; }
    public EventOccurrence Occurrence { get; set; } = null!;
    public int ReminderDefinitionId { get; set; }
    public EventReminderDefinition ReminderDefinition { get; set; } = null!;
    public bool Value { get; set; }
}
