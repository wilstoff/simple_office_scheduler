using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services;
using SimpleOfficeScheduler.Services.Events;

namespace SimpleOfficeScheduler.Controllers;

[ApiController]
[Route("api/events")]
public class EventsApiController : ControllerBase
{
    private readonly IEventService _eventService;
    private readonly CalendarUpdateNotifier _notifier;

    public EventsApiController(IEventService eventService, CalendarUpdateNotifier notifier)
    {
        _eventService = eventService;
        _notifier = notifier;
    }

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("calendar")]
    public async Task<IActionResult> GetCalendarFeed([FromQuery] DateTime start, [FromQuery] DateTime end)
    {
        // FullCalendar sends UTC range params — convert to LocalDateTime for the query
        // Pad by ±14 hours to cover all possible timezone offsets
        var rangeStart = LocalDateTime.FromDateTime(start).PlusHours(-14);
        var rangeEnd = LocalDateTime.FromDateTime(end).PlusHours(14);

        var occurrences = await _eventService.GetOccurrencesInRangeAsync(rangeStart, rangeEnd);

        var result = occurrences.Select(o =>
        {
            // Convert wall-clock time to UTC for FullCalendar
            var startUtc = TimeZoneHelper.WallClockToUtc(o.StartTime.ToDateTimeUnspecified(), o.Event.TimeZoneId);
            var endUtc = TimeZoneHelper.WallClockToUtc(o.EndTime.ToDateTimeUnspecified(), o.Event.TimeZoneId);

            return new
            {
                id = o.Id.ToString(),
                title = o.Event.Title,
                start = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = endUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                color = o.IsCancelled ? "#ccc" : (o.Signups.Count >= o.Event.Capacity ? "#ffc107" : "#0d6efd"),
                url = $"/events/{o.EventId}",
                extendedProps = new
                {
                    capacity = o.Event.Capacity,
                    signedUp = o.Signups.Count,
                    isCancelled = o.IsCancelled,
                    eventId = o.EventId,
                    owner = o.Event.Owner.DisplayName,
                    timeZoneId = o.Event.TimeZoneId
                }
            };
        });

        return Ok(result);
    }

    [HttpGet("search")]
    [Authorize]
    public async Task<IActionResult> Search([FromQuery] string? q)
    {
        var events = await _eventService.SearchEventsAsync(q);
        return Ok(events.Select(MapEventResponse));
    }

    [HttpGet("{id:int}")]
    [Authorize]
    public async Task<IActionResult> GetEvent(int id)
    {
        var evt = await _eventService.GetEventAsync(id);
        if (evt is null) return NotFound();
        return Ok(MapEventResponse(evt));
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateEvent([FromBody] CreateEventRequest request)
    {
        var evt = new Event
        {
            Title = request.Title,
            Description = request.Description,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Capacity = request.Capacity,
            TimeZoneId = request.TimeZoneId ?? TimeZoneHelper.GetLocalTimeZoneId(),
            Recurrence = request.Recurrence is not null ? new RecurrencePattern
            {
                Type = request.Recurrence.Type,
                DaysOfWeek = request.Recurrence.DaysOfWeek,
                Interval = request.Recurrence.Interval,
                RecurrenceEndDate = request.Recurrence.RecurrenceEndDate,
                MaxOccurrences = request.Recurrence.MaxOccurrences
            } : null
        };

        var created = await _eventService.CreateEventAsync(evt, GetUserId());
        _notifier.Notify();
        var full = await _eventService.GetEventAsync(created.Id);
        return CreatedAtAction(nameof(GetEvent), new { id = created.Id }, MapEventResponse(full!));
    }

    [HttpPut("{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateEvent(int id, [FromBody] UpdateEventRequest request)
    {
        var evt = new Event
        {
            Id = id,
            Title = request.Title,
            Description = request.Description,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            Capacity = request.Capacity,
            TimeZoneId = request.TimeZoneId ?? TimeZoneHelper.GetLocalTimeZoneId(),
            Recurrence = request.Recurrence is not null ? new RecurrencePattern
            {
                Type = request.Recurrence.Type,
                DaysOfWeek = request.Recurrence.DaysOfWeek,
                Interval = request.Recurrence.Interval,
                RecurrenceEndDate = request.Recurrence.RecurrenceEndDate,
                MaxOccurrences = request.Recurrence.MaxOccurrences
            } : null
        };

        var (success, error) = await _eventService.UpdateEventAsync(evt, GetUserId());
        if (!success) return BadRequest(new { error });

        _notifier.Notify();
        var updated = await _eventService.GetEventAsync(id);
        return Ok(MapEventResponse(updated!));
    }

    [HttpPost("{eventId:int}/signup/{occurrenceId:int}")]
    [Authorize]
    public async Task<IActionResult> SignUp(int eventId, int occurrenceId, [FromBody] SignUpRequest? request = null)
    {
        var (success, error) = await _eventService.SignUpAsync(occurrenceId, GetUserId(), request?.Message);
        if (!success) return BadRequest(new { error });
        _notifier.Notify();
        return Ok();
    }

    [HttpDelete("{eventId:int}/signup/{occurrenceId:int}")]
    [Authorize]
    public async Task<IActionResult> CancelSignUp(int eventId, int occurrenceId)
    {
        var (success, error) = await _eventService.CancelSignUpAsync(occurrenceId, GetUserId());
        if (!success) return BadRequest(new { error });
        _notifier.Notify();
        return Ok();
    }

    [HttpPost("occurrences/{occurrenceId:int}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelOccurrence(int occurrenceId)
    {
        var (success, error) = await _eventService.CancelOccurrenceAsync(occurrenceId, GetUserId());
        if (!success) return BadRequest(new { error });
        _notifier.Notify();
        return Ok();
    }

    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteEvent(int id)
    {
        var (success, error) = await _eventService.DeleteEventAsync(id, GetUserId());
        if (!success)
        {
            if (error == "Event not found.") return NotFound(new { error });
            return BadRequest(new { error });
        }
        _notifier.Notify();
        return Ok();
    }

    [HttpPost("{id:int}/transfer")]
    [Authorize]
    public async Task<IActionResult> TransferOwnership(int id, [FromQuery] int newOwnerId)
    {
        var (success, error) = await _eventService.TransferOwnershipAsync(id, GetUserId(), newOwnerId);
        if (!success) return BadRequest(new { error });
        _notifier.Notify();
        return Ok();
    }

    private static EventResponse MapEventResponse(Event evt) => new()
    {
        Id = evt.Id,
        Title = evt.Title,
        Description = evt.Description,
        OwnerUserId = evt.OwnerUserId,
        OwnerDisplayName = evt.Owner?.DisplayName ?? "",
        StartTime = evt.StartTime,
        EndTime = evt.EndTime,
        Capacity = evt.Capacity,
        TimeZoneId = evt.TimeZoneId,
        Recurrence = evt.Recurrence is not null ? new RecurrencePatternDto
        {
            Type = evt.Recurrence.Type,
            DaysOfWeek = evt.Recurrence.DaysOfWeek,
            Interval = evt.Recurrence.Interval,
            RecurrenceEndDate = evt.Recurrence.RecurrenceEndDate,
            MaxOccurrences = evt.Recurrence.MaxOccurrences
        } : null,
        Occurrences = evt.Occurrences?.Select(o =>
        {
            var zone = TimeZoneHelper.GetZone(evt.TimeZoneId);
            var startUtc = o.StartTime.InZoneLeniently(zone).ToInstant();
            var endUtc = o.EndTime.InZoneLeniently(zone).ToInstant();

            return new OccurrenceResponse
            {
                Id = o.Id,
                EventId = o.EventId,
                StartTime = o.StartTime,
                EndTime = o.EndTime,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                TimeZoneId = evt.TimeZoneId,
                IsCancelled = o.IsCancelled,
                SignupCount = o.Signups?.Count ?? 0,
                Signups = o.Signups?.Select(s => new SignupResponse
                {
                    UserId = s.UserId,
                    DisplayName = s.User?.DisplayName ?? "",
                    SignedUpAt = s.SignedUpAt,
                    Message = s.Message
                }).ToList() ?? new()
            };
        }).OrderBy(o => o.StartTime).ToList() ?? new()
    };
}
