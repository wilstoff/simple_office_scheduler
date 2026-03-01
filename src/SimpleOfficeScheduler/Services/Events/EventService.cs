using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Calendar;
using SimpleOfficeScheduler.Services.Recurrence;

namespace SimpleOfficeScheduler.Services.Events;

public class EventService : IEventService
{
    private readonly AppDbContext _db;
    private readonly RecurrenceExpander _expander;
    private readonly ICalendarInviteService _calendarService;
    private readonly RecurrenceSettings _recurrenceSettings;
    private readonly IClock _clock;
    private readonly ILogger<EventService> _logger;
    private readonly CalendarUpdateNotifier _notifier;

    public EventService(
        AppDbContext db,
        RecurrenceExpander expander,
        ICalendarInviteService calendarService,
        IOptions<RecurrenceSettings> recurrenceSettings,
        IClock clock,
        ILogger<EventService> logger,
        CalendarUpdateNotifier notifier)
    {
        _db = db;
        _expander = expander;
        _calendarService = calendarService;
        _recurrenceSettings = recurrenceSettings.Value;
        _clock = clock;
        _logger = logger;
        _notifier = notifier;
    }

    private Instant Now => _clock.GetCurrentInstant();

    private LocalDateTime NowInEventTimeZone(Event evt)
    {
        var zone = TimeZoneHelper.GetZone(evt.TimeZoneId);
        return Now.InZone(zone).LocalDateTime;
    }

    public async Task<Event> CreateEventAsync(Event evt, int ownerUserId)
    {
        if (evt.EndTime.CompareTo(evt.StartTime) <= 0)
            throw new ArgumentException("End time must be after start time.");

        evt.OwnerUserId = ownerUserId;
        evt.DurationMinutes = (int)Period.Between(evt.StartTime, evt.EndTime).ToDuration().TotalMinutes;
        evt.CreatedAt = Now;
        evt.UpdatedAt = Now;

        // Resolve timezone ID (validate or fall back to default)
        evt.TimeZoneId = TimeZoneHelper.ResolveTimeZoneId(evt.TimeZoneId);

        _db.Events.Add(evt);
        await _db.SaveChangesAsync();

        // Generate occurrences
        var nowInTz = NowInEventTimeZone(evt);
        var horizon = nowInTz.PlusMonths(_recurrenceSettings.DefaultHorizonMonths);
        var dates = _expander.Expand(evt, horizon);

        foreach (var (start, end) in dates)
        {
            _db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = evt.Id,
                StartTime = start,
                EndTime = end
            });
        }

        await _db.SaveChangesAsync();
        _notifier.Notify();
        return evt;
    }

    public async Task<Event?> GetEventAsync(int eventId)
    {
        return await _db.Events
            .Include(e => e.Owner)
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Signups)
                    .ThenInclude(s => s.User)
            .FirstOrDefaultAsync(e => e.Id == eventId);
    }

    public async Task<List<Event>> SearchEventsAsync(string? searchTerm)
    {
        var query = _db.Events
            .Include(e => e.Owner)
            .Include(e => e.Occurrences)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.ToLower();
            query = query.Where(e =>
                e.Title.ToLower().Contains(term) ||
                (e.Description != null && e.Description.ToLower().Contains(term)) ||
                e.Owner.DisplayName.ToLower().Contains(term));
        }

        return await query.OrderBy(e => e.StartTime).ToListAsync();
    }

    public async Task<List<EventOccurrence>> GetOccurrencesInRangeAsync(LocalDateTime start, LocalDateTime end)
    {
        return await _db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
            .Where(o => o.StartTime >= start && o.StartTime <= end)
            .OrderBy(o => o.StartTime)
            .ToListAsync();
    }

    public async Task<EventOccurrence?> GetOccurrenceAsync(int occurrenceId)
    {
        return await _db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
                .ThenInclude(s => s.User)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);
    }

    public async Task<(bool Success, string? Error)> SignUpAsync(int occurrenceId, int userId, string? message = null)
    {
        var occurrence = await _db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.IsCancelled)
            return (false, "This occurrence has been cancelled.");

        if (occurrence.Signups.Any(s => s.UserId == userId))
            return (false, "You are already signed up for this occurrence.");

        if (occurrence.Signups.Count >= occurrence.Event.Capacity)
            return (false, "This occurrence is full.");

        var user = await _db.Users.FindAsync(userId);
        if (user is null)
            return (false, "User not found.");

        var signup = new EventSignup
        {
            EventOccurrenceId = occurrenceId,
            UserId = userId,
            SignedUpAt = Now,
            Message = message
        };

        _db.EventSignups.Add(signup);
        await _db.SaveChangesAsync();

        // Send calendar invite
        try
        {
            if (string.IsNullOrEmpty(occurrence.GraphEventId))
            {
                var graphEventId = await _calendarService.CreateMeetingAsync(occurrence, occurrence.Event.Owner, user);
                occurrence.GraphEventId = graphEventId;
                await _db.SaveChangesAsync();
            }
            else
            {
                await _calendarService.AddAttendeeAsync(occurrence.GraphEventId, occurrence.Event.Owner, user);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send calendar invite for occurrence {OccurrenceId} (Event: {EventTitle}, User: {UserId}, GraphEventId: {GraphEventId})",
                occurrenceId, occurrence.Event.Title, userId, occurrence.GraphEventId);
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> CancelSignUpAsync(int occurrenceId, int userId)
    {
        var signup = await _db.EventSignups
            .FirstOrDefaultAsync(s => s.EventOccurrenceId == occurrenceId && s.UserId == userId);

        if (signup is null)
            return (false, "You are not signed up for this occurrence.");

        _db.EventSignups.Remove(signup);
        await _db.SaveChangesAsync();

        // Remove from calendar invite
        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        if (!string.IsNullOrEmpty(occurrence?.GraphEventId))
        {
            try
            {
                var user = await _db.Users.FindAsync(userId);
                if (user is not null)
                    await _calendarService.RemoveAttendeeAsync(occurrence.GraphEventId, user);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove attendee from calendar invite for occurrence {OccurrenceId} (User: {UserId}, GraphEventId: {GraphEventId})",
                    occurrenceId, userId, occurrence.GraphEventId);
            }
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> CancelOccurrenceAsync(int occurrenceId, int userId)
    {
        var occurrence = await _db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can cancel occurrences.");

        occurrence.IsCancelled = true;
        await _db.SaveChangesAsync();

        // Cancel calendar invite if exists
        if (!string.IsNullOrEmpty(occurrence.GraphEventId))
        {
            try
            {
                await _calendarService.CancelMeetingAsync(occurrence.GraphEventId, occurrence.Event.Owner);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel calendar invite for occurrence {OccurrenceId} (Event: {EventTitle}, GraphEventId: {GraphEventId})",
                    occurrenceId, occurrence.Event.Title, occurrence.GraphEventId);
            }

            occurrence.GraphEventId = null;
            await _db.SaveChangesAsync();
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UncancelOccurrenceAsync(int occurrenceId, int userId)
    {
        var occurrence = await _db.EventOccurrences
            .Include(o => o.Event)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can uncancel occurrences.");

        if (!occurrence.IsCancelled)
            return (false, "This occurrence is not cancelled.");

        occurrence.IsCancelled = false;
        await _db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateEventAsync(Event updatedEvent, int userId)
    {
        var existing = await _db.Events
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Signups)
            .FirstOrDefaultAsync(e => e.Id == updatedEvent.Id);

        if (existing is null)
            return (false, "Event not found.");

        if (existing.OwnerUserId != userId)
            return (false, "Only the event owner can modify this event.");

        if (updatedEvent.EndTime.CompareTo(updatedEvent.StartTime) <= 0)
            return (false, "End time must be after start time.");

        // Update basic properties
        existing.Title = updatedEvent.Title;
        existing.Description = updatedEvent.Description;
        existing.StartTime = updatedEvent.StartTime;
        existing.EndTime = updatedEvent.EndTime;
        existing.DurationMinutes = (int)Period.Between(updatedEvent.StartTime, updatedEvent.EndTime).ToDuration().TotalMinutes;
        existing.Capacity = updatedEvent.Capacity;
        existing.TimeZoneId = TimeZoneHelper.ResolveTimeZoneId(updatedEvent.TimeZoneId);
        existing.Recurrence = updatedEvent.Recurrence;
        existing.UpdatedAt = Now;

        var nowInTz = NowInEventTimeZone(existing);

        // Remove future occurrences without signups and regenerate
        var futureOccurrencesWithoutSignups = existing.Occurrences
            .Where(o => o.StartTime.CompareTo(nowInTz) > 0 && !o.Signups.Any())
            .ToList();

        foreach (var occ in futureOccurrencesWithoutSignups)
        {
            _db.EventOccurrences.Remove(occ);
        }

        // Regenerate occurrences
        var horizon = nowInTz.PlusMonths(_recurrenceSettings.DefaultHorizonMonths);
        var dates = _expander.Expand(existing, horizon);

        // Only add occurrences that don't already exist
        var existingStartTimes = existing.Occurrences
            .Where(o => !futureOccurrencesWithoutSignups.Contains(o))
            .Select(o => o.StartTime)
            .ToHashSet();

        foreach (var (start, end) in dates)
        {
            if (!existingStartTimes.Contains(start) && start.CompareTo(nowInTz) > 0)
            {
                _db.EventOccurrences.Add(new EventOccurrence
                {
                    EventId = existing.Id,
                    StartTime = start,
                    EndTime = end
                });
            }
        }

        await _db.SaveChangesAsync();
        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteEventAsync(int eventId, int userId)
    {
        var existing = await _db.Events
            .Include(e => e.Owner)
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Signups)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (existing is null)
            return (false, "Event not found.");

        if (existing.OwnerUserId != userId)
            return (false, "Only the event owner can delete this event.");

        // Cancel calendar invites for non-cancelled occurrences
        foreach (var occ in existing.Occurrences.Where(o => !o.IsCancelled && !string.IsNullOrEmpty(o.GraphEventId)))
        {
            try
            {
                await _calendarService.CancelMeetingAsync(occ.GraphEventId!, existing.Owner);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel calendar invite for occurrence {OccurrenceId} (Event: {EventTitle}, GraphEventId: {GraphEventId})",
                    occ.Id, existing.Title, occ.GraphEventId);
            }
        }

        _db.Events.Remove(existing);
        await _db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> TransferOwnershipAsync(int eventId, int currentOwnerId, int newOwnerId)
    {
        var evt = await _db.Events.FindAsync(eventId);
        if (evt is null)
            return (false, "Event not found.");

        if (evt.OwnerUserId != currentOwnerId)
            return (false, "Only the current owner can transfer ownership.");

        var newOwner = await _db.Users.FindAsync(newOwnerId);
        if (newOwner is null)
            return (false, "New owner not found.");

        evt.OwnerUserId = newOwnerId;
        evt.UpdatedAt = Now;
        await _db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }
}
