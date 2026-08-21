using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Calendar;
using SimpleOfficeScheduler.Services.Recurrence;
using SimpleOfficeScheduler.Services.Rooms;

namespace SimpleOfficeScheduler.Services.Events;

public class EventService : IEventService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RecurrenceExpander _expander;
    private readonly ICalendarInviteService _calendarService;
    private readonly IRoomService _roomService;
    private readonly RecurrenceSettings _recurrenceSettings;
    private readonly GraphApiSettings _graphSettings;
    private readonly IClock _clock;
    private readonly ILogger<EventService> _logger;
    private readonly CalendarUpdateNotifier _notifier;

    public EventService(
        IDbContextFactory<AppDbContext> dbFactory,
        RecurrenceExpander expander,
        ICalendarInviteService calendarService,
        IRoomService roomService,
        IOptions<RecurrenceSettings> recurrenceSettings,
        IOptions<GraphApiSettings> graphSettings,
        IClock clock,
        ILogger<EventService> logger,
        CalendarUpdateNotifier notifier)
    {
        _dbFactory = dbFactory;
        _expander = expander;
        _calendarService = calendarService;
        _roomService = roomService;
        _recurrenceSettings = recurrenceSettings.Value;
        _graphSettings = graphSettings.Value;
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

    /// <summary>
    /// The creator and every co-owner have identical management rights. Callers must have loaded
    /// CoOwners, or co-owners silently lose access.
    /// </summary>
    private static bool CanManage(Event evt, int userId) =>
        evt.OwnerUserId == userId || evt.CoOwners.Any(o => o.UserId == userId);

    /// <summary>How much runway is left before a series range gets rolled forward.</summary>
    private const int SeriesRenewalLeadDays = 30;

    /// <summary>
    /// How far out a workshop's Graph series may currently run. Rolled forward by
    /// RecurrenceExpansionBackgroundService as the booking window advances.
    /// </summary>
    private LocalDate SeriesWindowEnd(Event evt) =>
        NowInEventTimeZone(evt).Date.PlusDays(_graphSettings.RoomBookingWindowDays);

    /// <summary>
    /// Resolves a room mailbox against the room list. Returns null for no room; throws when the
    /// address is not a known room, so a typo fails loudly instead of silently booking nothing.
    /// </summary>
    private async Task<Room?> ResolveRoomAsync(string? roomEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(roomEmail)) return null;

        var rooms = await _roomService.GetRoomsAsync(ct);
        var room = rooms.FirstOrDefault(r =>
            string.Equals(r.Email, roomEmail, StringComparison.OrdinalIgnoreCase));

        if (room is null)
            throw new ArgumentException($"'{roomEmail}' is not a known conference room.");

        return room;
    }

    /// <summary>Every owner of the event: the creator first, then co-owners.</summary>
    private static async Task<List<AppUser>> LoadOwnersAsync(AppDbContext db, Event evt)
    {
        var ids = new List<int> { evt.OwnerUserId };
        ids.AddRange(evt.CoOwners
            .Select(o => o.UserId)
            .Where(id => id != evt.OwnerUserId)
            .Distinct());

        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync();
        return ids.Select(id => users.First(u => u.Id == id)).ToList();
    }

    public async Task<Event> CreateEventAsync(Event evt, int ownerUserId, List<int>? coOwnerUserIds = null)
    {
        if (evt.EndTime.CompareTo(evt.StartTime) <= 0)
            throw new ArgumentException("End time must be after start time.");

        // A numbered Graph recurrence range cannot be rolled forward as the room booking window
        // advances, so workshops use an end-date range only. See GraphRecurrenceMapper.
        if (evt.EventType == EventType.Workshop && evt.Recurrence?.MaxOccurrences is not null)
            throw new ArgumentException("Workshops cannot use a maximum occurrence count. Set a recurrence end date instead.");

        // Resolved before anything is written, so an unknown room fails without leaving an event.
        var room = await ResolveRoomAsync(evt.RoomEmail);
        evt.RoomDisplayName = room?.DisplayName;

        await using var db = await _dbFactory.CreateDbContextAsync();

        evt.OwnerUserId = ownerUserId;
        evt.DurationMinutes = (int)Period.Between(evt.StartTime, evt.EndTime).ToDuration().TotalMinutes;
        evt.CreatedAt = Now;
        evt.UpdatedAt = Now;

        // Resolve timezone ID (validate or fall back to default)
        evt.TimeZoneId = TimeZoneHelper.ResolveTimeZoneId(evt.TimeZoneId);

        db.Events.Add(evt);
        await db.SaveChangesAsync();

        // Co-owners, excluding the creator who is always an owner via OwnerUserId
        var coOwnerIds = (coOwnerUserIds ?? new List<int>())
            .Where(id => id != ownerUserId)
            .Distinct()
            .ToList();

        foreach (var coOwnerId in coOwnerIds)
        {
            // Adding to the DbSet is enough: EF's relationship fixup puts the row on
            // evt.CoOwners too, and adding it by hand as well duplicates it on the invite.
            db.EventOwners.Add(new EventOwner { EventId = evt.Id, UserId = coOwnerId });
        }

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
                EndTime = end,
                RoomBookingStatus = room is null ? RoomBookingStatus.None : RoomBookingStatus.Pending
            });
        }

        await db.SaveChangesAsync();

        // A workshop's meeting exists from creation with the owning team on it, rather than being
        // created lazily by the first signup the way office hours are.
        if (evt.EventType == EventType.Workshop)
        {
            try
            {
                var owners = await LoadOwnersAsync(db, evt);
                var windowEnd = SeriesWindowEnd(evt);
                evt.GraphSeriesId = await _calendarService.CreateSeriesAsync(evt, owners, windowEnd, room);
                evt.GraphSeriesWindowEnd = windowEnd;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Teams series for workshop {EventId} ('{Title}')",
                    evt.Id, evt.Title);
            }
        }

        _notifier.Notify();
        return evt;
    }

    public async Task<Event?> GetEventAsync(int eventId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Events
            .Include(e => e.Owner)
            .Include(e => e.CoOwners)
                .ThenInclude(o => o.User)
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
            .Include(e => e.CoOwners)
                .ThenInclude(o => o.User)
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
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
                    .ThenInclude(co => co.User)
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
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
                    .ThenInclude(co => co.User)
            .Include(o => o.Signups)
                .ThenInclude(s => s.User)
            .Include(o => o.Contributors)
                .ThenInclude(c => c.User)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);
    }

    public async Task<(bool Success, string? Error)> SignUpAsync(int occurrenceId, int userId, string message)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.Owner)
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
                    .ThenInclude(co => co.User)
            .Include(o => o.Signups)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType == EventType.TechMeeting && !occurrence.IsLightningTalks)
            return (false, "Signups are not available for this occurrence. Contributors are assigned by the owner.");

        // Workshop attendees join an existing meeting, so there is nothing for a message to seed.
        if (occurrence.Event.EventType != EventType.Workshop && string.IsNullOrWhiteSpace(message))
            return (false, "A message is required when signing up.");

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
            if (occurrence.Event.EventType == EventType.Workshop)
            {
                await SyncWorkshopInstanceAttendeesAsync(db, occurrence, allSignups);
            }
            else if (string.IsNullOrEmpty(occurrence.GraphEventId))
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

    /// <summary>
    /// Pushes the current attendee list onto a workshop occurrence's series instance. Graph records
    /// the change as an exception on the series, so the rest of the series is untouched. The
    /// resolved instance id is cached on the occurrence to avoid re-querying instances every time.
    /// </summary>
    private async Task SyncWorkshopInstanceAttendeesAsync(
        AppDbContext db, EventOccurrence occurrence, IReadOnlyList<EventSignup> signups)
    {
        if (string.IsNullOrEmpty(occurrence.Event.GraphSeriesId))
        {
            _logger.LogWarning("Workshop {EventId} has no Graph series; skipping attendee sync for occurrence {OccurrenceId}",
                occurrence.EventId, occurrence.Id);
            return;
        }

        // A one-off workshop has no instances to expand: the Graph object created up front IS the
        // meeting for its single occurrence. Asking Graph for its instances fails outright with
        // "ExpandSeries can only be performed against a series."
        var targetId = occurrence.Event.Recurrence is null
            ? occurrence.Event.GraphSeriesId
            : occurrence.GraphEventId;

        if (string.IsNullOrEmpty(targetId))
        {
            targetId = await _calendarService.GetInstanceIdAsync(
                occurrence.Event.GraphSeriesId, occurrence.StartTime, occurrence.Event.TimeZoneId);

            if (string.IsNullOrEmpty(targetId)) return;

            // Cache the resolved instance so later signups skip the lookup.
            occurrence.GraphEventId = targetId;
            await db.SaveChangesAsync();
        }

        var owners = await LoadOwnersAsync(db, occurrence.Event);
        await _calendarService.PatchInstanceAttendeesAsync(targetId, owners, signups);
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
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
                    .ThenInclude(co => co.User)
            .Include(o => o.Signups)
                .ThenInclude(s => s.User)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);
        if (occurrence?.Event.EventType == EventType.Workshop)
        {
            // The owning team keeps the meeting whether or not anyone is signed up, so this only
            // ever narrows the instance's attendee list.
            try
            {
                await SyncWorkshopInstanceAttendeesAsync(db, occurrence, occurrence.Signups.ToList());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update workshop series instance for occurrence {OccurrenceId} (User: {UserId})",
                    occurrenceId, userId);
            }
        }
        else if (!string.IsNullOrEmpty(occurrence?.GraphEventId))
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
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (!CanManage(occurrence.Event, userId))
            return (false, "Only an event owner can cancel occurrences.");

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
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (!CanManage(occurrence.Event, userId))
            return (false, "Only an event owner can uncancel occurrences.");

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
            .Include(e => e.CoOwners)
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Signups)
            .FirstOrDefaultAsync(e => e.Id == updatedEvent.Id);

        if (existing is null)
            return (false, "Event not found.");

        if (!CanManage(existing, userId))
            return (false, "Only an event owner can modify this event.");

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
                    EndTime = end,
                    RoomBookingStatus = existing.RoomEmail is null
                        ? RoomBookingStatus.None
                        : RoomBookingStatus.Pending
                });
            }
        }

        // Cached instance ids belong to the old schedule, so drop them and let the next signup
        // resolve against the updated series.
        foreach (var occ in existing.Occurrences.Where(o =>
            o.StartTime.CompareTo(nowInTz) > 0 && o.GraphEventId is not null))
        {
            occ.GraphEventId = null;
        }

        await db.SaveChangesAsync();

        // Without this the app and Graph drift apart: the occurrence list changes while attendees
        // keep whatever meeting was created originally. Turning a workshop recurring after the fact
        // appeared to do nothing for exactly this reason.
        if (existing.EventType == EventType.Workshop)
            await SyncWorkshopSeriesAsync(db, existing);

        _notifier.Notify();
        return (true, null);
    }

    /// <summary>
    /// Pushes a workshop's current schedule onto its Graph series, creating the series first if it
    /// does not have one yet. A missing series is what an event is left with when the Graph call
    /// failed at creation time, so an edit is the natural place to recover.
    /// </summary>
    private async Task SyncWorkshopSeriesAsync(AppDbContext db, Event evt)
    {
        var windowEnd = SeriesWindowEnd(evt);

        try
        {
            if (string.IsNullOrEmpty(evt.GraphSeriesId))
            {
                // Callers load CoOwners as part of the permission check, so no extra query here.
                var owners = await LoadOwnersAsync(db, evt);
                var room = await ResolveRoomAsync(evt.RoomEmail);
                evt.GraphSeriesId = await _calendarService.CreateSeriesAsync(evt, owners, windowEnd, room);
            }
            else
            {
                await _calendarService.UpdateSeriesScheduleAsync(evt.GraphSeriesId, evt, windowEnd);
            }

            evt.GraphSeriesWindowEnd = windowEnd;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync the Teams series for workshop {EventId} ('{Title}', GraphSeriesId: {GraphSeriesId})",
                evt.Id, evt.Title, evt.GraphSeriesId);
        }
    }

    public async Task<(bool Success, string? Error)> DeleteEventAsync(int eventId, int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.Events
            .Include(e => e.Owner)
            .Include(e => e.CoOwners)
            .Include(e => e.Occurrences)
                .ThenInclude(o => o.Signups)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (existing is null)
            return (false, "Event not found.");

        if (!CanManage(existing, userId))
            return (false, "Only an event owner can delete this event.");

        if (!string.IsNullOrEmpty(existing.GraphSeriesId))
        {
            // One series covers every occurrence, so cancelling it once removes them all.
            try
            {
                await _calendarService.CancelMeetingAsync(existing.GraphSeriesId, existing.Owner);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel Teams series for event {EventId} ('{Title}', GraphSeriesId: {GraphSeriesId})",
                    existing.Id, existing.Title, existing.GraphSeriesId);
            }
        }
        else
        {
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
        }

        db.Events.Remove(existing);
        await db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    public async Task<(bool Success, string? Error)> TransferOwnershipAsync(int eventId, int currentOwnerId, int newOwnerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var evt = await db.Events
            .Include(e => e.CoOwners)
            .FirstOrDefaultAsync(e => e.Id == eventId);
        if (evt is null)
            return (false, "Event not found.");

        // Transfer stays creator-only. Co-owners manage the event but cannot reassign who owns it.
        if (evt.OwnerUserId != currentOwnerId)
            return (false, "Only the current owner can transfer ownership.");

        var newOwner = await db.Users.FindAsync(newOwnerId);
        if (newOwner is null)
            return (false, "New owner not found.");

        evt.OwnerUserId = newOwnerId;
        evt.UpdatedAt = Now;

        // The new owner is now an owner via OwnerUserId, so a co-owner row for them is redundant.
        var redundant = evt.CoOwners.Where(o => o.UserId == newOwnerId).ToList();
        if (redundant.Count > 0)
            db.EventOwners.RemoveRange(redundant);

        await db.SaveChangesAsync();

        _notifier.Notify();
        return (true, null);
    }

    /// <summary>
    /// Changes or clears the event's room. Future occurrences go back to Pending because the new
    /// room has to accept the booking on its own terms; a room that declines shows up through
    /// RefreshRoomBookingStatusAsync rather than being assumed to have worked.
    /// </summary>
    public async Task<(bool Success, string? Error)> SetRoomAsync(int eventId, int userId, string? roomEmail)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var evt = await db.Events
            .Include(e => e.CoOwners)
            .Include(e => e.Occurrences)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (evt is null)
            return (false, "Event not found.");

        if (!CanManage(evt, userId))
            return (false, "Only an event owner can change the room.");

        Room? room;
        try
        {
            room = await ResolveRoomAsync(roomEmail);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message);
        }

        evt.RoomEmail = room?.Email;
        evt.RoomDisplayName = room?.DisplayName;
        evt.UpdatedAt = Now;

        var nowInTz = NowInEventTimeZone(evt);
        foreach (var occ in evt.Occurrences.Where(o => o.StartTime.CompareTo(nowInTz) > 0))
        {
            occ.RoomBookingStatus = room is null ? RoomBookingStatus.None : RoomBookingStatus.Pending;
            occ.RoomBookingError = null;
        }

        await db.SaveChangesAsync();

        try
        {
            if (!string.IsNullOrEmpty(evt.GraphSeriesId))
            {
                await _calendarService.UpdateSeriesRoomAsync(evt.GraphSeriesId, room);
            }
            else
            {
                // Office hours and tech meetings create a Graph event per occurrence, so the room
                // is applied to each one that already exists.
                foreach (var occ in evt.Occurrences.Where(o =>
                    !o.IsCancelled && !string.IsNullOrEmpty(o.GraphEventId)))
                {
                    await _calendarService.UpdateSeriesRoomAsync(occ.GraphEventId!, room);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the room on the calendar for event {EventId} (Room: {Room})",
                eventId, room?.Email ?? "(none)");

            foreach (var occ in evt.Occurrences.Where(o => o.RoomBookingStatus == RoomBookingStatus.Pending))
            {
                occ.RoomBookingStatus = RoomBookingStatus.Failed;
                occ.RoomBookingError = ex.Message;
            }
            await db.SaveChangesAsync();
        }

        _notifier.Notify();
        return (true, null);
    }

    /// <summary>
    /// Reads back what each room mailbox did with its booking. Rooms reply asynchronously, so a
    /// Pending occurrence stays Pending until the booking attendant responds.
    /// </summary>
    /// <returns>How many occurrences changed status.</returns>
    public async Task<int> RefreshRoomBookingStatusAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pending = await db.EventOccurrences
            .Include(o => o.Event)
            .Where(o => o.RoomBookingStatus == RoomBookingStatus.Pending
                && !o.IsCancelled
                && o.Event.RoomEmail != null)
            .ToListAsync(ct);

        var changed = 0;

        foreach (var occ in pending)
        {
            // A workshop instance without its own exception is covered by the series master.
            var graphId = occ.GraphEventId ?? occ.Event.GraphSeriesId;
            if (string.IsNullOrEmpty(graphId)) continue;

            try
            {
                var outcome = await _calendarService.GetRoomResponseAsync(graphId, occ.Event.RoomEmail!);
                if (outcome is null) continue;

                occ.RoomBookingStatus = outcome.Status;
                occ.RoomBookingError = outcome.Error;
                changed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read the room response for occurrence {OccurrenceId} (Event: {EventTitle})",
                    occ.Id, occ.Event.Title);
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            _notifier.Notify();
        }

        return changed;
    }

    /// <summary>
    /// Rolls the recurrence range forward on any workshop series that is close to lapsing. Room
    /// mailboxes refuse bookings beyond BookingWindowInDays, so the series never runs further out
    /// than RoomBookingWindowDays and instead creeps forward as time passes. Patching the range
    /// re-sends the update to the resource attendee, which is what makes the room evaluate the newly
    /// added dates.
    /// </summary>
    /// <returns>How many series were extended.</returns>
    public async Task<int> ExtendExpiringWorkshopSeriesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var candidates = await db.Events
            .Include(e => e.CoOwners)
            .Where(e => e.EventType == EventType.Workshop
                && e.GraphSeriesId != null
                && e.Recurrence != null)
            .ToListAsync(ct);

        var extended = 0;

        foreach (var evt in candidates)
        {
            var target = SeriesWindowEnd(evt);
            var current = evt.GraphSeriesWindowEnd;

            // Nothing to do until the current range is inside the renewal lead time.
            if (current is not null
                && current.Value.CompareTo(NowInEventTimeZone(evt).Date.PlusDays(SeriesRenewalLeadDays)) > 0)
                continue;

            // A workshop that ends inside the current range has no further dates to book.
            var recurrenceEnd = evt.Recurrence!.RecurrenceEndDate;
            if (recurrenceEnd is not null && current is not null
                && recurrenceEnd.Value.CompareTo(current.Value) <= 0)
                continue;

            if (current is not null && target.CompareTo(current.Value) <= 0)
                continue;

            try
            {
                await _calendarService.ExtendSeriesRangeAsync(evt.GraphSeriesId!, evt, target);
                evt.GraphSeriesWindowEnd = target;
                evt.UpdatedAt = Now;
                await db.SaveChangesAsync(ct);
                extended++;
            }
            catch (Exception ex)
            {
                // Leave GraphSeriesWindowEnd alone so the next pass retries this series.
                _logger.LogError(ex, "Failed to extend Teams series for workshop {EventId} ('{Title}') to {WindowEnd}",
                    evt.Id, evt.Title, target);
            }
        }

        if (extended > 0)
            _notifier.Notify();

        return extended;
    }

    /// <summary>
    /// Replaces the co-owner list. The creator is always an owner through Event.OwnerUserId and
    /// must not appear here, so an empty list leaves them as the sole owner.
    /// </summary>
    public async Task<(bool Success, string? Error)> SetCoOwnersAsync(int eventId, int userId, List<int> coOwnerUserIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var evt = await db.Events
            .Include(e => e.CoOwners)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (evt is null)
            return (false, "Event not found.");

        if (!CanManage(evt, userId))
            return (false, "Only an event owner can manage co-owners.");

        var requested = coOwnerUserIds.Distinct().ToList();

        if (requested.Contains(evt.OwnerUserId))
            return (false, "The event creator is already an owner and cannot be listed as a co-owner.");

        var foundCount = await db.Users.CountAsync(u => requested.Contains(u.Id));
        if (foundCount != requested.Count)
            return (false, "One or more users were not found.");

        db.EventOwners.RemoveRange(evt.CoOwners.Where(o => !requested.Contains(o.UserId)));

        var existingIds = evt.CoOwners.Select(o => o.UserId).ToHashSet();
        foreach (var id in requested.Where(id => !existingIds.Contains(id)))
        {
            db.EventOwners.Add(new EventOwner { EventId = eventId, UserId = id });
        }

        evt.UpdatedAt = Now;
        await db.SaveChangesAsync();

        // Reload so the series attendee list reflects what was just saved.
        if (!string.IsNullOrEmpty(evt.GraphSeriesId))
        {
            try
            {
                await db.Entry(evt).Collection(e => e.CoOwners).LoadAsync();
                var owners = await LoadOwnersAsync(db, evt);
                await _calendarService.UpdateSeriesOwnersAsync(evt.GraphSeriesId, owners);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Teams series owners for event {EventId} (GraphSeriesId: {GraphSeriesId})",
                    eventId, evt.GraphSeriesId);
            }
        }

        _notifier.Notify();
        return (true, null);
    }

    // ── Tech Meeting: Contributors ──────────────────────────────────

    public async Task<(bool Success, string? Error)> SetContributorsAsync(int occurrenceId, int userId, List<int> contributorUserIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var occurrence = await db.EventOccurrences
            .Include(o => o.Event)
                .ThenInclude(e => e.CoOwners)
            .Include(o => o.Contributors)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType != EventType.TechMeeting)
            return (false, "Contributors can only be assigned to tech meeting events.");

        if (!CanManage(occurrence.Event, userId))
            return (false, "Only an event owner can assign contributors.");

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
                .ThenInclude(e => e.CoOwners)
            .Include(o => o.Contributors)
            .Include(o => o.Signups)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType != EventType.TechMeeting)
            return (false, "Lightning talks are only available for tech meeting events.");

        if (!CanManage(occurrence.Event, userId))
            return (false, "Only an event owner can toggle lightning talks.");

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
                .ThenInclude(e => e.CoOwners)
            .Include(o => o.Contributors)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (occurrence.Event.EventType is not (EventType.TechMeeting or EventType.Workshop))
            return (false, "Occurrence names can only be edited for tech meeting and workshop events.");

        var isOwner = CanManage(occurrence.Event, userId);
        var isContributor = occurrence.Contributors.Any(c => c.UserId == userId);

        if (!isOwner && !isContributor)
            return (false, "Only an event owner or an assigned contributor can edit the occurrence name.");

        if (namePrefix is not null)
        {
            if (!isOwner)
                return (false, "Only an event owner can edit the name prefix.");
            occurrence.NamePrefix = namePrefix;
        }

        if (nameSuffix is not null)
        {
            occurrence.NameSuffix = nameSuffix;
        }

        await db.SaveChangesAsync();

        // Keep the existing Teams meeting subject in sync with the new topic/name.
        if (occurrence.GraphEventId is not null)
        {
            await _calendarService.UpdateMeetingSubjectAsync(occurrence.GraphEventId, occurrence.DisplayName);
        }

        _notifier.Notify();
        return (true, null);
    }

    // ── Tech Meeting: Reminders ─────────────────────────────────────

    public async Task<(bool Success, string? Error)> SetReminderDefinitionsAsync(int eventId, int userId, List<string> reminderNames)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var evt = await db.Events
            .Include(e => e.CoOwners)
            .Include(e => e.ReminderDefinitions)
            .FirstOrDefaultAsync(e => e.Id == eventId);

        if (evt is null)
            return (false, "Event not found.");

        if (!CanManage(evt, userId))
            return (false, "Only an event owner can manage reminders.");

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
                .ThenInclude(e => e.CoOwners)
            .Include(o => o.ReminderValues)
            .FirstOrDefaultAsync(o => o.Id == occurrenceId);

        if (occurrence is null)
            return (false, "Occurrence not found.");

        if (!CanManage(occurrence.Event, userId))
            return (false, "Only an event owner can set reminder values.");

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
