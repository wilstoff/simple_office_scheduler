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

    public EventsApiController(IEventService eventService)
    {
        _eventService = eventService;
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

            var isTechMeeting = o.Event.EventType == EventType.TechMeeting;
            var isWorkshop = o.Event.EventType == EventType.Workshop;
            var isLightningTalks = o.IsLightningTalks;
            var effectiveCapacity = o.LightningTalksCapacity ?? o.Event.Capacity;
            var color = o.IsCancelled ? "#ccc"
                : isLightningTalks && o.Signups.Count >= effectiveCapacity ? "#ffc107"
                : isTechMeeting ? "#198754"
                : o.Signups.Count >= o.Event.Capacity ? "#ffc107"
                : isWorkshop ? "#0dcaf0"
                : "#0d6efd";

            return new
            {
                id = o.Id.ToString(),
                title = isTechMeeting || isWorkshop ? o.DisplayName : o.Event.Title,
                start = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                end = endUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                color,
                url = $"/events/{o.EventId}",
                extendedProps = new
                {
                    capacity = o.LightningTalksCapacity ?? o.Event.Capacity,
                    signedUp = o.Signups.Count,
                    isCancelled = o.IsCancelled,
                    eventId = o.EventId,
                    owner = o.Event.Owner.DisplayName,
                    timeZoneId = o.Event.TimeZoneId,
                    eventType = o.Event.EventType.ToString(),
                    isLightningTalks = o.IsLightningTalks,
                    contributors = o.Contributors?.Select(c => c.User.DisplayName).ToList() ?? new List<string>(),
                    owners = new[] { o.Event.Owner.DisplayName }
                        .Concat(o.Event.CoOwners?.Select(co => co.User.DisplayName) ?? Enumerable.Empty<string>())
                        .ToList(),
                    room = o.Event.RoomDisplayName,
                    roomBookingStatus = o.RoomBookingStatus.ToString()
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
            EventType = request.EventType,
            RoomEmail = request.RoomEmail,
            Recurrence = request.Recurrence is not null ? new RecurrencePattern
            {
                Type = request.Recurrence.Type,
                DaysOfWeek = request.Recurrence.DaysOfWeek,
                Interval = request.Recurrence.Interval,
                RecurrenceEndDate = request.Recurrence.RecurrenceEndDate,
                MaxOccurrences = request.Recurrence.MaxOccurrences
            } : null
        };

        Event created;
        try
        {
            created = await _eventService.CreateEventAsync(evt, GetUserId(), request.CoOwnerIds);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

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


        var updated = await _eventService.GetEventAsync(id);
        return Ok(MapEventResponse(updated!));
    }

    [HttpPost("{eventId:int}/signup/{occurrenceId:int}")]
    [Authorize]
    public async Task<IActionResult> SignUp(int eventId, int occurrenceId, [FromBody] SignUpRequest request)
    {
        var (success, error) = await _eventService.SignUpAsync(occurrenceId, GetUserId(), request.Message);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpDelete("{eventId:int}/signup/{occurrenceId:int}")]
    [Authorize]
    public async Task<IActionResult> CancelSignUp(int eventId, int occurrenceId)
    {
        var (success, error) = await _eventService.CancelSignUpAsync(occurrenceId, GetUserId());
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPost("occurrences/{occurrenceId:int}/cancel")]
    [Authorize]
    public async Task<IActionResult> CancelOccurrence(int occurrenceId)
    {
        var (success, error) = await _eventService.CancelOccurrenceAsync(occurrenceId, GetUserId());
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPost("occurrences/{occurrenceId:int}/uncancel")]
    [Authorize]
    public async Task<IActionResult> UncancelOccurrence(int occurrenceId)
    {
        var (success, error) = await _eventService.UncancelOccurrenceAsync(occurrenceId, GetUserId());
        if (!success) return BadRequest(new { error });

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

        return Ok();
    }

    [HttpPost("{id:int}/transfer")]
    [Authorize]
    public async Task<IActionResult> TransferOwnership(int id, [FromQuery] int newOwnerId)
    {
        var (success, error) = await _eventService.TransferOwnershipAsync(id, GetUserId(), newOwnerId);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPost("{id:int}/co-owners")]
    [Authorize]
    public async Task<IActionResult> SetCoOwners(int id, [FromBody] SetCoOwnersRequest request)
    {
        var (success, error) = await _eventService.SetCoOwnersAsync(id, GetUserId(), request.UserIds);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPost("{id:int}/room")]
    [Authorize]
    public async Task<IActionResult> SetRoom(int id, [FromBody] SetRoomRequest request)
    {
        var (success, error) = await _eventService.SetRoomAsync(id, GetUserId(), request.RoomEmail);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    // ── Tech Meeting Endpoints ──────────────────────────────────────

    [HttpPost("occurrences/{occurrenceId:int}/contributors")]
    [Authorize]
    public async Task<IActionResult> SetContributors(int occurrenceId, [FromBody] SetContributorsRequest request)
    {
        var (success, error) = await _eventService.SetContributorsAsync(occurrenceId, GetUserId(), request.UserIds);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPost("occurrences/{occurrenceId:int}/lightning-talks")]
    [Authorize]
    public async Task<IActionResult> ToggleLightningTalks(int occurrenceId, [FromBody] ToggleLightningTalksRequest request)
    {
        var (success, error) = await _eventService.ToggleLightningTalksAsync(occurrenceId, GetUserId(), request.IsLightningTalks, request.Capacity);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPatch("occurrences/{occurrenceId:int}/name")]
    [Authorize]
    public async Task<IActionResult> UpdateOccurrenceName(int occurrenceId, [FromBody] UpdateOccurrenceNameRequest request)
    {
        var (success, error) = await _eventService.UpdateOccurrenceNameAsync(occurrenceId, GetUserId(), request.NamePrefix, request.NameSuffix);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    // ── Reminder Endpoints ────────────────────────────────────────

    [HttpPut("{id:int}/reminders")]
    [Authorize]
    public async Task<IActionResult> SetReminderDefinitions(int id, [FromBody] SetReminderDefinitionsRequest request)
    {
        var (success, error) = await _eventService.SetReminderDefinitionsAsync(id, GetUserId(), request.Names);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    [HttpPost("occurrences/{occurrenceId:int}/reminders/{definitionId:int}")]
    [Authorize]
    public async Task<IActionResult> SetReminderValue(int occurrenceId, int definitionId, [FromBody] SetReminderValueRequest request)
    {
        var (success, error) = await _eventService.SetReminderValueAsync(occurrenceId, GetUserId(), definitionId, request.Value);
        if (!success) return BadRequest(new { error });

        return Ok();
    }

    // ── Response Mapping ────────────────────────────────────────────

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
        EventType = evt.EventType,
        Recurrence = evt.Recurrence is not null ? new RecurrencePatternDto
        {
            Type = evt.Recurrence.Type,
            DaysOfWeek = evt.Recurrence.DaysOfWeek,
            Interval = evt.Recurrence.Interval,
            RecurrenceEndDate = evt.Recurrence.RecurrenceEndDate,
            MaxOccurrences = evt.Recurrence.MaxOccurrences
        } : null,
        RoomEmail = evt.RoomEmail,
        RoomDisplayName = evt.RoomDisplayName,
        CoOwners = evt.CoOwners?.Select(o => new CoOwnerResponse
        {
            UserId = o.UserId,
            DisplayName = o.User?.DisplayName ?? ""
        }).ToList() ?? new(),
        ReminderDefinitions = evt.ReminderDefinitions?.OrderBy(d => d.DisplayOrder).Select(d => new ReminderDefinitionResponse
        {
            Id = d.Id,
            Name = d.Name,
            DisplayOrder = d.DisplayOrder
        }).ToList() ?? new(),
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
                IsLightningTalks = o.IsLightningTalks,
                LightningTalksCapacity = o.LightningTalksCapacity,
                NamePrefix = o.NamePrefix,
                NameSuffix = o.NameSuffix,
                DisplayName = o.DisplayName,
                RoomBookingStatus = o.RoomBookingStatus,
                RoomBookingError = o.RoomBookingError,
                SignupCount = o.Signups?.Count ?? 0,
                Signups = o.Signups?.Select(s => new SignupResponse
                {
                    UserId = s.UserId,
                    DisplayName = s.User?.DisplayName ?? "",
                    SignedUpAt = s.SignedUpAt,
                    Message = s.Message
                }).ToList() ?? new(),
                Contributors = o.Contributors?.Select(c => new ContributorResponse
                {
                    UserId = c.UserId,
                    DisplayName = c.User?.DisplayName ?? ""
                }).ToList() ?? new(),
                ReminderValues = o.ReminderValues?.Select(v => new ReminderValueResponse
                {
                    ReminderDefinitionId = v.ReminderDefinitionId,
                    Value = v.Value
                }).ToList() ?? new()
            };
        }).OrderBy(o => o.StartTime).ToList() ?? new()
    };
}
