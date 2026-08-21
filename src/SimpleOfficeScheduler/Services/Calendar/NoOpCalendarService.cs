using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Calendar;

public class NoOpCalendarService : ICalendarInviteService
{
    private readonly ILogger<NoOpCalendarService> _logger;

    public NoOpCalendarService(ILogger<NoOpCalendarService> logger)
    {
        _logger = logger;
    }

    public Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee, IReadOnlyList<EventSignup> allSignups)
    {
        _logger.LogInformation("DEV: Would create Teams meeting for '{Title}' with {Owner} and {Signee}",
            occurrence.Event.Title, owner.Email, signee.Email);
        LogSignupTopics(allSignups);
        return Task.FromResult("fake-graph-id-" + Guid.NewGuid());
    }

    public Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee, IReadOnlyList<EventSignup> allSignups)
    {
        _logger.LogInformation("DEV: Would add attendee {Email} to meeting {GraphEventId}",
            newSignee.Email, graphEventId);
        LogSignupTopics(allSignups);
        return Task.CompletedTask;
    }

    public Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove, IReadOnlyList<EventSignup> remainingSignups)
    {
        _logger.LogInformation("DEV: Would remove attendee {Email} from meeting {GraphEventId}",
            attendeeToRemove.Email, graphEventId);
        LogSignupTopics(remainingSignups);
        return Task.CompletedTask;
    }

    public Task CancelMeetingAsync(string graphEventId, AppUser owner)
    {
        _logger.LogInformation("DEV: Would cancel meeting {GraphEventId}", graphEventId);
        return Task.CompletedTask;
    }

    public Task<string> CreateMeetingForContributorsAsync(EventOccurrence occurrence, AppUser owner, IReadOnlyList<AppUser> contributors)
    {
        _logger.LogInformation("DEV: Would create Teams meeting for '{Title}' with {Owner} and {Count} contributors: {Contributors}",
            occurrence.DisplayName, owner.Email, contributors.Count,
            string.Join(", ", contributors.Select(c => c.DisplayName)));
        if (!string.IsNullOrEmpty(occurrence.NameSuffix))
            _logger.LogInformation("DEV:   Topic: {Topic}", occurrence.NameSuffix);
        return Task.FromResult("fake-graph-id-" + Guid.NewGuid());
    }

    public Task UpdateMeetingAttendeesAsync(string graphEventId, AppUser owner, IReadOnlyList<AppUser> contributors)
    {
        _logger.LogInformation("DEV: Would update attendees for meeting {GraphEventId} with {Count} contributors: {Contributors}",
            graphEventId, contributors.Count,
            string.Join(", ", contributors.Select(c => c.DisplayName)));
        return Task.CompletedTask;
    }

    public Task UpdateMeetingSubjectAsync(string graphEventId, string subject)
    {
        _logger.LogInformation("DEV: Would update subject for meeting {GraphEventId} to '{Subject}'",
            graphEventId, subject);
        return Task.CompletedTask;
    }

    // ── Recurring series (workshops) ────────────────────────────────

    public Task<string> CreateSeriesAsync(Event evt, IReadOnlyList<AppUser> owners, LocalDate windowEnd)
    {
        _logger.LogInformation("DEV: Would create Teams series for workshop '{Title}' through {WindowEnd} with {Count} owners: {Owners}",
            evt.Title, windowEnd, owners.Count, string.Join(", ", owners.Select(o => o.DisplayName)));
        return Task.FromResult("fake-graph-series-" + Guid.NewGuid());
    }

    public Task ExtendSeriesRangeAsync(string graphSeriesId, Event evt, LocalDate newWindowEnd)
    {
        _logger.LogInformation("DEV: Would extend Teams series {GraphEventId} range to {WindowEnd}",
            graphSeriesId, newWindowEnd);
        return Task.CompletedTask;
    }

    public Task<string?> GetInstanceIdAsync(string graphSeriesId, LocalDateTime occurrenceStart, string timeZoneId)
    {
        _logger.LogInformation("DEV: Would resolve instance of series {GraphEventId} at {Start} ({TimeZone})",
            graphSeriesId, occurrenceStart, timeZoneId);
        return Task.FromResult<string?>($"{graphSeriesId}-instance-{occurrenceStart:yyyyMMddHHmm}");
    }

    public Task PatchInstanceAttendeesAsync(string instanceId, IReadOnlyList<AppUser> owners, IReadOnlyList<EventSignup> signups)
    {
        _logger.LogInformation("DEV: Would patch attendees on series instance {InstanceId}: {OwnerCount} owners, {SignupCount} signups",
            instanceId, owners.Count, signups.Count);
        LogSignupTopics(signups);
        return Task.CompletedTask;
    }

    public Task UpdateSeriesOwnersAsync(string graphSeriesId, IReadOnlyList<AppUser> owners)
    {
        _logger.LogInformation("DEV: Would set owners on Teams series {GraphEventId} to {Count}: {Owners}",
            graphSeriesId, owners.Count, string.Join(", ", owners.Select(o => o.DisplayName)));
        return Task.CompletedTask;
    }

    private void LogSignupTopics(IReadOnlyList<EventSignup> signups)
    {
        foreach (var s in signups.Where(s => !string.IsNullOrWhiteSpace(s.Message)))
        {
            _logger.LogInformation("DEV:   {User}: \"{Topic}\"", s.User?.DisplayName ?? "Unknown", s.Message);
        }
    }
}
