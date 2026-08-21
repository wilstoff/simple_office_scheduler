using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Calendar;

public interface ICalendarInviteService
{
    Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee, IReadOnlyList<EventSignup> allSignups);
    Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee, IReadOnlyList<EventSignup> allSignups);
    Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove, IReadOnlyList<EventSignup> remainingSignups);
    Task CancelMeetingAsync(string graphEventId, AppUser owner);
    Task<string> CreateMeetingForContributorsAsync(EventOccurrence occurrence, AppUser owner, IReadOnlyList<AppUser> contributors);
    Task UpdateMeetingAttendeesAsync(string graphEventId, AppUser owner, IReadOnlyList<AppUser> contributors);
    Task UpdateMeetingSubjectAsync(string graphEventId, string subject);

    // ── Recurring series (workshops) ────────────────────────────────
    // Workshops are backed by a single Graph recurring series created up front, rather than one
    // standalone Graph event per occurrence. Per-occurrence attendee changes patch a series
    // instance, which Graph records as an exception.

    /// <summary>
    /// Creates the recurring series for a workshop with its owners as required attendees.
    /// <paramref name="windowEnd"/> bounds the recurrence range so it stays inside the room
    /// mailbox booking window; it is rolled forward later by ExtendSeriesRangeAsync.
    /// </summary>
    Task<string> CreateSeriesAsync(Event evt, IReadOnlyList<AppUser> owners, LocalDate windowEnd);

    /// <summary>Pushes the series recurrence range end date forward.</summary>
    Task ExtendSeriesRangeAsync(string graphSeriesId, Event evt, LocalDate newWindowEnd);

    /// <summary>Resolves the Graph instance id for one occurrence of a series.</summary>
    Task<string?> GetInstanceIdAsync(string graphSeriesId, LocalDateTime occurrenceStart, string timeZoneId);

    /// <summary>Replaces the attendee list on a single series instance.</summary>
    Task PatchInstanceAttendeesAsync(string instanceId, IReadOnlyList<AppUser> owners, IReadOnlyList<EventSignup> signups);

    /// <summary>Replaces the attendee list on the series master.</summary>
    Task UpdateSeriesOwnersAsync(string graphSeriesId, IReadOnlyList<AppUser> owners);
}
