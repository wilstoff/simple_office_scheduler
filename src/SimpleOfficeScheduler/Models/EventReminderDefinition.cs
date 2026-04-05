namespace SimpleOfficeScheduler.Models;

public class EventReminderDefinition
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public Event Event { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}
