namespace SimpleOfficeScheduler.Models;

public class OccurrenceContributor
{
    public int Id { get; set; }
    public int EventOccurrenceId { get; set; }
    public EventOccurrence Occurrence { get; set; } = null!;
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
}
