using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Calendar;

public interface ICalendarInviteService
{
    Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee, IReadOnlyList<EventSignup> allSignups);
    Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee, IReadOnlyList<EventSignup> allSignups);
    Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove, IReadOnlyList<EventSignup> remainingSignups);
    Task CancelMeetingAsync(string graphEventId, AppUser owner);
}
