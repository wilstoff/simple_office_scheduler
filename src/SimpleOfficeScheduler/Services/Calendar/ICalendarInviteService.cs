using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Calendar;

public interface ICalendarInviteService
{
    Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee);
    Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee);
    Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove);
    Task CancelMeetingAsync(string graphEventId, AppUser owner);
}
