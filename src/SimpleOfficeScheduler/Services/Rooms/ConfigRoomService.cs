using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Rooms;

/// <summary>
/// Serves the room list from configuration. Used when Graph is not configured, and as the fallback
/// inside GraphRoomService when a /places call fails. Free/busy needs Graph, so availability is
/// always unknown here.
/// </summary>
public class ConfigRoomService : IRoomService
{
    private readonly GraphApiSettings _settings;
    private readonly ILogger<ConfigRoomService> _logger;

    public ConfigRoomService(IOptions<GraphApiSettings> settings, ILogger<ConfigRoomService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<Room>> GetRoomsAsync(CancellationToken ct = default)
    {
        var rooms = _settings.Rooms.Select(r => new Room
        {
            Email = r.Email,
            DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Email : r.DisplayName,
            Capacity = r.Capacity,
            Building = r.Building,
            FloorLabel = r.FloorLabel
        }).ToList();

        if (rooms.Count == 0)
            _logger.LogDebug("No rooms are configured under GraphApi:Rooms.");

        return Task.FromResult<IReadOnlyList<Room>>(rooms);
    }

    public Task<IReadOnlyList<RoomAvailability>?> GetAvailabilityAsync(
        IReadOnlyList<string> roomEmails,
        LocalDateTime start,
        LocalDateTime end,
        string timeZoneId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RoomAvailability>?>(null);
}
