using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Calendar;

public interface ICalendarInviteService
{
    Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee);
    Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee);
    Task CancelMeetingAsync(string graphEventId, AppUser owner);
}
