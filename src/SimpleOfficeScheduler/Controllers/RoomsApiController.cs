using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Rooms;

namespace SimpleOfficeScheduler.Controllers;

[ApiController]
[Route("api/rooms")]
[Authorize]
public class RoomsApiController : ControllerBase
{
    private readonly IRoomService _roomService;

    public RoomsApiController(IRoomService roomService)
    {
        _roomService = roomService;
    }

    [HttpGet]
    public async Task<IActionResult> GetRooms(CancellationToken ct)
    {
        var rooms = await _roomService.GetRoomsAsync(ct);
        return Ok(rooms.Select(Map));
    }

    /// <summary>
    /// Rooms with free/busy for one window. IsBusy is null on every room when availability could
    /// not be read, which callers must render as "unknown" rather than "free".
    /// </summary>
    [HttpGet("availability")]
    public async Task<IActionResult> GetAvailability(
        [FromQuery] LocalDateTime start,
        [FromQuery] LocalDateTime end,
        [FromQuery] string? timeZoneId,
        CancellationToken ct)
    {
        var rooms = await _roomService.GetRoomsAsync(ct);
        if (rooms.Count == 0) return Ok(Array.Empty<RoomResponse>());

        var availability = await _roomService.GetAvailabilityAsync(
            rooms.Select(r => r.Email).ToList(),
            start,
            end,
            Services.TimeZoneHelper.ResolveTimeZoneId(timeZoneId),
            ct);

        var byEmail = availability?.ToDictionary(a => a.Email, StringComparer.OrdinalIgnoreCase);

        return Ok(rooms.Select(r =>
        {
            var response = Map(r);
            if (byEmail is not null && byEmail.TryGetValue(r.Email, out var a))
                response.IsBusy = a.IsBusy;
            return response;
        }));
    }

    private static RoomResponse Map(Room r) => new()
    {
        Email = r.Email,
        DisplayName = r.DisplayName,
        Capacity = r.Capacity,
        Building = r.Building,
        FloorLabel = r.FloorLabel
    };
}
