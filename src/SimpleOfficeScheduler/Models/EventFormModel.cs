using System.ComponentModel.DataAnnotations;
using NodaTime;

namespace SimpleOfficeScheduler.Models;

/// <summary>One choice in the co-owner picker: the user id plus what to show for it.</summary>
public record CoOwnerChoice(int UserId, string DisplayName);

/// <summary>
/// The event form as the create page and the calendar side panel both bind it. Shared on purpose:
/// each component used to hold its own copy of this class and its own copy of the mapping below,
/// and the copies diverged — the panel stopped carrying the selected room onto the Event, so a room
/// picked there was discarded on create and the meeting went out with no room on it.
/// </summary>
public class EventFormModel
{
    [Required]
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int Capacity { get; set; } = 1;
    public string TimeZoneId { get; set; } = string.Empty;
    public EventType EventType { get; set; } = EventType.OfficeHours;
    public List<CoOwnerChoice> CoOwners { get; set; } = new();
    public string? RoomEmail { get; set; }
    public bool IsRecurring { get; set; }
    public RecurrenceType RecurrenceType { get; set; } = RecurrenceType.Weekly;
    public int Interval { get; set; } = 1;
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public DateTime? RecurrenceEndDate { get; set; }

    /// <summary>
    /// Builds the Event to hand to CreateEventAsync or UpdateEventAsync. Co-owners are not part of
    /// it: they are passed alongside on create and applied by SetCoOwnersAsync on edit.
    /// </summary>
    public Event ToEvent() => new()
    {
        Title = Title,
        Description = Description,
        StartTime = LocalDateTime.FromDateTime(StartTime),
        EndTime = LocalDateTime.FromDateTime(EndTime),
        Capacity = Capacity,
        TimeZoneId = TimeZoneId,
        EventType = EventType,
        RoomEmail = RoomEmail,
        // A null Recurrence is what makes RecurrenceExpander return a single occurrence, so the
        // pattern is only built when the form actually asked for one.
        Recurrence = IsRecurring
            ? new RecurrencePattern
            {
                Type = RecurrenceType,
                Interval = Interval,
                DaysOfWeek = DaysOfWeek,
                RecurrenceEndDate = RecurrenceEndDate.HasValue
                    ? LocalDate.FromDateTime(RecurrenceEndDate.Value)
                    : null
            }
            : null
    };
}
