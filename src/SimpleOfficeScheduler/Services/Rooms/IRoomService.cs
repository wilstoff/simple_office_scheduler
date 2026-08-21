using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Rooms;

public interface IRoomService
{
    /// <summary>Bookable rooms, cached for the process lifetime.</summary>
    Task<IReadOnlyList<Room>> GetRoomsAsync(CancellationToken ct = default);

    /// <summary>
    /// Free/busy for the given rooms over one window, or null when the data cannot be read. Callers
    /// must treat null as "unknown" and never as "free".
    /// </summary>
    Task<IReadOnlyList<RoomAvailability>?> GetAvailabilityAsync(
        IReadOnlyList<string> roomEmails,
        LocalDateTime start,
        LocalDateTime end,
        string timeZoneId,
        CancellationToken ct = default);
}
