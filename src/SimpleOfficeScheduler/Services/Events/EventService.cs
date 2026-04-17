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
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RecurrenceExpander _expander;
    private readonly ICalendarInviteService _calendarService;
    private readonly RecurrenceSettings _recurrenceSettings;
    private readonly IClock _clock;
    private readonly ILogger<EventService> _logger;
    private readonly CalendarUpdateNotifier _notifier;

    public EventService(
        IDbContextFactory<AppDbContext> dbFactory,
        RecurrenceExpander expander,
        ICalendarInviteService calendarService,
        IOptions<RecurrenceSettings> recurrenceSettings,
        IClock clock,
        ILogger<EventService> logger,
        CalendarUpdateNotifier notifier)
    {
        _dbFactory = dbFactory;
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

        await using var db = await _dbFactory.CreateDbContextAsync();

        evt.OwnerUserId = ownerUserId;
        evt.DurationMinutes = (int)Period.Between(evt.StartTime, evt.EndTime).ToDuration().TotalMinutes;
        evt.CreatedAt = Now;
        evt.UpdatedAt = Now;

        // Resolve timezone ID (validate or fall back to default)
        evt.TimeZoneId = TimeZoneHelper.ResolveTimeZoneId(evt.TimeZoneId);

        db.Events.Add(evt);
        await db.SaveChangesAsync();

        // Generate occurrences
        var nowInTz = NowInEventTimeZone(evt);
        var horizon = nowInTz.PlusMonths(_recurrenceSettings.DefaultHorizonMonths);
        var dates = _expander.Expand(evt, horizon);

        foreach (var (start, end) in dates)
        {
            db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = evt.Id,
                StartTime = start,
                EndTime = end
            });
        }

        await db.SaveChangesAsync();
        _notifier.Notify();
        return evt;
    }

    public async Task<Event?> GetEventAsync(int eventId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Events
            .Include(e => e.Owner)
            .Include(e => e.ReminderDefinitions.OrderBy(d => d.DisplayOrder))
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Signups)
                    .ThenInclude(s => s.User)
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Contributors)
                    .ThenInclude(c => c.User)
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.ReminderValues)
            .FirstOrDefaultAsync(e => e.Id == eventId);
    }

    public async Task<List<Event>> SearchEventsAsync(string? searchTerm)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var query = db.Events
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
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
            .Include(o => o.Contributors)
                .ThenInclude(c => c.User)
            .Where(o => o.StartTime >= start && o.StartTime <= end)
            .OrderBy(o => o.StartTime)
            .ToListAsync();
    }

    public async Task<EventOccurrence?> GetOccurrenceAsync(int occurrenceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
                .ThenInclude(s => s.User)
            .Include(o => o.Contributors)
                .ThenInclude(c => c.User)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);
    }

    public async Task<(bool Success, string? Error)> SignUpAsync(int occurrenceId, int userId, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return (false, "A message is required when signing up.");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType == EventType.TechMeeting && !occurrence.IsLightningTalks)
            return (false, "Signups are not available for this occurrence. Contributors are assigned by the owner.");

        if (occurrence.IsCancelled)
            return (false, "This occurrence has been cancelled.");

        if (occurrence.Signups.Any(s => s.UserId == userId))
            return (false, "You are already signed up for this occurrence.");

        var effectiveCapacity = occurrence.LightningTalksCapacity ?? occurrence.Event.Capacity;
        if (occurrence.Signups.Count >= effectiveCapacity)
            return (false, "This occurrence is full.");

        var user = await db.Users.FindAsync(userId);
        if (user is null)
            return (false, "User not found.");

        var signup = new EventSignup
        {
            EventOccurrenceId = occurrenceId,
            UserId = userId,
            SignedUpAt = Now,
            Message = message
        };

        db.EventSignups.Add(signup);
        await db.SaveChangesAsync();

        // Reload signups with User navigation for calendar body
        var allSignups = await db.EventSignups
            .Include(s => s.User)
            .Where(s => s.EventOccurrenceId == occurrenceId)
            .ToListAsync();

        // Send calendar invite
        try
        {
            if (string.IsNullOrEmpty(occurrence.GraphEventId))
            {
                var graphEventId = await _calendarService.CreateMeetingAsync(occurrence, occurrence.Event.Owner, user, allSignups);
                occurrence.GraphEventId = graphEventId;
                await db.SaveChangesAsync();
            }
            else
            {
                await _calendarService.AddAttendeeAsync(occurrence.GraphEventId, occurrence.Event.Owner, user, allSignups);
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
        await using var db = await _dbFactory.CreateDbContextAsync();

        var signup = await db.EventSignups
            .FirstOrDefaultAsync(s => s.EventOccurrenceId == occurrenceId && s.UserId == userId);

        if (signup is null)
            return (false, "You are not signed up for this occurrence.");

        db.EventSignups.Remove(signup);
        await db.SaveChangesAsync();

        // Update calendar invite
        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Signups)
                .ThenInclude(s => s.User)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);
        if (!string.IsNullOrEmpty(occurrence?.GraphEventId))
        {
            try
            {
                if (!occurrence.Signups.Any())
                {
                    // Last signup removed — cancel the entire meeting
                    await _calendarService.CancelMeetingAsync(occurrence.GraphEventId, occurrence.Event.Owner);
                    occurrence.GraphEventId = null;
                    await db.SaveChangesAsync();
                }
                else
                {
                    var user = await db.Users.FindAsync(userId);
                    if (user is not null)
                        await _calendarService.RemoveAttendeeAsync(occurrence.GraphEventId, user, occurrence.Signups.ToList());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update calendar invite for occurrence {OccurrenceId} (User: {UserId}, GraphEventId: {GraphEventId})",
                    occurrenceId, userId, occurrence.GraphEventId);
            }
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> CancelOccurrenceAsync(int occurrenceId, int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can cancel occurrences.");

        occurrence.IsCancelled = true;
        await db.SaveChangesAsync();

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
            await db.SaveChangesAsync();
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UncancelOccurrenceAsync(int occurrenceId, int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can uncancel occurrences.");

        if (!occurrence.IsCancelled)
            return (false, "This occurrence is not cancelled.");

        occurrence.IsCancelled = false;
        await db.SaveChangesAsync();

        // Recreate calendar invite if there are existing signups
        var signups = await db.EventSignups
            .Include(s => s.User)
            .Where(s => s.EventOccurrenceId == occurrenceId)
            .ToListAsync();

        if (signups.Any())
        {
            try
            {
                var firstSignup = signups[0];
                var graphEventId = await _calendarService.CreateMeetingAsync(
                    occurrence, occurrence.Event.Owner, firstSignup.User, signups);
                occurrence.GraphEventId = graphEventId;
                await db.SaveChangesAsync();

                foreach (var signup in signups.Skip(1))
                {
                    await _calendarService.AddAttendeeAsync(graphEventId, occurrence.Event.Owner, signup.User, signups);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recreate calendar invite for occurrence {OccurrenceId} (Event: {EventTitle}, GraphEventId: {GraphEventId})",
                    occurrenceId, occurrence.Event.Title, occurrence.GraphEventId);
            }
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateEventAsync(Event updatedEvent, int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.Events
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
            db.EventOccurrences.Remove(occ);
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
                db.EventOccurrences.Add(new EventOccurrence
                {
                    EventId = existing.Id,
                    StartTime = start,
                    EndTime = end
                });
            }
        }

        await db.SaveChangesAsync();
        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> DeleteEventAsync(int eventId, int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.Events
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

        db.Events.Remove(existing);
        await db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> TransferOwnershipAsync(int eventId, int currentOwnerId, int newOwnerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var evt = await db.Events.FindAsync(eventId);
        if (evt is null)
            return (false, "Event not found.");

        if (evt.OwnerUserId != currentOwnerId)
            return (false, "Only the current owner can transfer ownership.");

        var newOwner = await db.Users.FindAsync(newOwnerId);
        if (newOwner is null)
            return (false, "New owner not found.");

        evt.OwnerUserId = newOwnerId;
        evt.UpdatedAt = Now;
        await db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    // ── Tech Meeting: Contributors ──────────────────────────────────

    public async Task<(bool Success, string? Error)> SetContributorsAsync(int occurrenceId, int userId, List<int> contributorUserIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
            .Include(o => o.Contributors)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType != EventType.TechMeeting)
            return (false, "Contributors can only be assigned to tech meeting events.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can assign contributors.");

        if (occurrence.IsLightningTalks)
            return (false, "Cannot assign contributors to a lightning talks occurrence.");

        // Remove existing contributors
        db.OccurrenceContributors.RemoveRange(occurrence.Contributors);

        // Add new contributors
        foreach (var contributorId in contributorUserIds)
        {
            db.OccurrenceContributors.Add(new OccurrenceContributor
            {
                EventOccurrenceId = occurrenceId,
                UserId = contributorId
            });
        }

        await db.SaveChangesAsync();

        // Calendar integration
        var owner = await db.Users.FindAsync(occurrence.Event.OwnerUserId);
        if (contributorUserIds.Count > 0)
        {
            var contributors = await db.Users
                .Where(u => contributorUserIds.Contains(u.Id))
                .ToListAsync();

            if (occurrence.GraphEventId is not null)
            {
                await _calendarService.UpdateMeetingAttendeesAsync(occurrence.GraphEventId, owner!, contributors);
            }
            else
            {
                var graphEventId = await _calendarService.CreateMeetingForContributorsAsync(occurrence, owner!, contributors);
                occurrence.GraphEventId = graphEventId;
                await db.SaveChangesAsync();
            }
        }
        else if (occurrence.GraphEventId is not null)
        {
            await _calendarService.CancelMeetingAsync(occurrence.GraphEventId, owner!);
            occurrence.GraphEventId = null;
            await db.SaveChangesAsync();
        }

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> ToggleLightningTalksAsync(int occurrenceId, int userId, bool isLightningTalks, int? capacity = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
            .Include(o => o.Contributors)
            .Include(o => o.Signups)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType != EventType.TechMeeting)
            return (false, "Lightning talks are only available for tech meeting events.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can toggle lightning talks.");

        var owner = await db.Users.FindAsync(occurrence.Event.OwnerUserId);
        var stateChanging = occurrence.IsLightningTalks != isLightningTalks;

        if (stateChanging)
        {
            if (isLightningTalks)
            {
                db.OccurrenceContributors.RemoveRange(occurrence.Contributors);
            }
            else
            {
                db.EventSignups.RemoveRange(occurrence.Signups);
            }

            if (occurrence.GraphEventId is not null)
            {
                await _calendarService.CancelMeetingAsync(occurrence.GraphEventId, owner!);
                occurrence.GraphEventId = null;
            }

            occurrence.NameSuffix = isLightningTalks ? "Lightning Talks" : null;
        }

        occurrence.IsLightningTalks = isLightningTalks;
        occurrence.LightningTalksCapacity = isLightningTalks ? capacity : null;
        await db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> UpdateOccurrenceNameAsync(int occurrenceId, int userId, string? namePrefix, string? nameSuffix)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
            .Include(o => o.Contributors)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType != EventType.TechMeeting)
            return (false, "Occurrence names can only be edited for tech meeting events.");

        var isOwner = occurrence.Event.OwnerUserId == userId;
        var isContributor = occurrence.Contributors.Any(c => c.UserId == userId);

        if (!isOwner && !isContributor)
            return (false, "Only the event owner or an assigned contributor can edit the occurrence name.");

        if (namePrefix is not null)
        {
            if (!isOwner)
                return (false, "Only the event owner can edit the name prefix.");
            occurrence.NamePrefix = namePrefix;
        }

        if (nameSuffix is not null)
        {
            occurrence.NameSuffix = nameSuffix;
        }

        await db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    // ── Tech Meeting: Reminders ─────────────────────────────────────

    public async Task<(bool Success, string? Error)> SetReminderDefinitionsAsync(int eventId, int userId, List<string> reminderNames)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var evt = await db.Events
            .Include(e => e.ReminderDefinitions)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (evt is null)
            return (false, "Event not found.");

        if (evt.OwnerUserId != userId)
            return (false, "Only the event owner can manage reminders.");

        if (evt.EventType != EventType.TechMeeting)
            return (false, "Reminders are only available for tech meeting events.");

        if (reminderNames.Count > 10)
            return (false, "Maximum 10 reminders allowed.");

        if (reminderNames.Any(string.IsNullOrWhiteSpace))
            return (false, "Reminder names cannot be empty.");

        if (reminderNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != reminderNames.Count)
            return (false, "Reminder names must be unique.");

        // Diff: keep existing by name, remove missing, add new
        var existingByName = evt.ReminderDefinitions.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);
        var toRemove = evt.ReminderDefinitions.Where(d => !reminderNames.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        db.EventReminderDefinitions.RemoveRange(toRemove);

        for (var i = 0; i < reminderNames.Count; i++)
        {
            if (existingByName.TryGetValue(reminderNames[i], out var existing))
            {
                existing.DisplayOrder = i;
            }
            else
            {
                db.EventReminderDefinitions.Add(new EventReminderDefinition
                {
                    EventId = eventId,
                    Name = reminderNames[i],
                    DisplayOrder = i
                });
            }
        }

        await db.SaveChangesAsync();
        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> SetReminderValueAsync(int occurrenceId, int userId, int reminderDefinitionId, bool value)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
            .Include(o => o.ReminderValues)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.OwnerUserId != userId)
            return (false, "Only the event owner can set reminder values.");

        var existing = occurrence.ReminderValues.FirstOrDefault(v => v.ReminderDefinitionId == reminderDefinitionId);
        if (existing is not null)
        {
            existing.Value = value;
        }
        else
        {
            db.OccurrenceReminderValues.Add(new OccurrenceReminderValue
            {
                EventOccurrenceId = occurrenceId,
                ReminderDefinitionId = reminderDefinitionId,
                Value = value
            });
        }

        await db.SaveChangesAsync();
        _notifier.Notify();
        return (true, null);
    }
}
