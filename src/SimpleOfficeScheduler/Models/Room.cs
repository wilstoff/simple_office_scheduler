namespace SimpleOfficeScheduler.Models;

/// <summary>
/// A bookable conference room. Not an entity: rooms live in Graph (or in config as a fallback) and
/// are referenced from an event by mailbox address.
/// </summary>
public class Room
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? Capacity { get; set; }
    public string? Building { get; set; }
    public string? FloorLabel { get; set; }
}

/// <summary>
/// A room's free/busy over the requested window. Null availability means the data could not be
/// read, which is deliberately distinct from "the room is free".
/// </summary>
public class RoomAvailability
{
    public string Email { get; set; } = string.Empty;
    public bool IsBusy { get; set; }

    /// <summary>Graph's availabilityView string, one character per slot.</summary>
    public string AvailabilityView { get; set; } = string.Empty;
}

public enum RoomBookingStatus
{
    None = 0,
    Pending = 1,
    Booked = 2,
    Declined = 3,
    Failed = 4
}

/// <summary>A room supplied through configuration, used when Graph is unavailable.</summary>
public class ConfiguredRoom
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int? Capacity { get; set; }
    public string? Building { get; set; }
    public string? FloorLabel { get; set; }
}
