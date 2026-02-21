namespace SimpleOfficeScheduler.Models;

public class RecurrencePattern
{
    public RecurrenceType Type { get; set; }
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public int Interval { get; set; } = 1;
    public DateTime? RecurrenceEndDate { get; set; }
    public int? MaxOccurrences { get; set; }
}
