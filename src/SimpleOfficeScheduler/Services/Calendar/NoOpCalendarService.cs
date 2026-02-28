using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Calendar;

public class NoOpCalendarService : ICalendarInviteService
{
    private readonly ILogger<NoOpCalendarService> _logger;

    public NoOpCalendarService(ILogger<NoOpCalendarService> logger)
    {
        _logger = logger;
    }

    public Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee)
    {
        _logger.LogInformation("DEV: Would create Teams meeting for '{Title}' with {Owner} and {Signee}",
            occurrence.Event.Title, owner.Email, signee.Email);
        return Task.FromResult("fake-graph-id-" + Guid.NewGuid());
    }

    public Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee)
    {
        _logger.LogInformation("DEV: Would add attendee {Email} to meeting {GraphEventId}",
            newSignee.Email, graphEventId);
        return Task.CompletedTask;
    }

    public Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove)
    {
        _logger.LogInformation("DEV: Would remove attendee {Email} from meeting {GraphEventId}",
            attendeeToRemove.Email, graphEventId);
        return Task.CompletedTask;
    }

    public Task CancelMeetingAsync(string graphEventId, AppUser owner)
    {
        _logger.LogInformation("DEV: Would cancel meeting {GraphEventId}", graphEventId);
        return Task.CompletedTask;
    }
}
