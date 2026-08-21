namespace SimpleOfficeScheduler.Models;

public class ActiveDirectorySettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 389;
    public bool UseSsl { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string SearchBase { get; set; } = string.Empty;
    public string? ServiceAccountUsername { get; set; }
    public string? ServiceAccountPassword { get; set; }
}

public class GraphApiSettings
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string TargetMailbox { get; set; } = string.Empty;

    /// <summary>
    /// How far ahead a workshop's Graph series is allowed to extend. Exchange room mailboxes refuse
    /// bookings past BookingWindowInDays (180 by default), and the app cannot read that value, so
    /// this stays a little under it. The series range is rolled forward as time passes.
    /// </summary>
    public int RoomBookingWindowDays { get; set; } = 170;

    /// <summary>
    /// Mailbox of a room list to scope room discovery to. When empty, every room in the tenant is
    /// listed. Either way this needs the Place.Read.All application permission.
    /// </summary>
    public string? RoomListEmail { get; set; }

    /// <summary>
    /// Rooms to offer when Graph cannot supply them, either because GraphApi is unconfigured or
    /// because Place.Read.All has not been consented to.
    /// </summary>
    public List<ConfiguredRoom> Rooms { get; set; } = new();
}

public class SeedUserSettings
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "testadmin";
    public string Password { get; set; } = "Test123!";
    public string DisplayName { get; set; } = "Test Admin";
    public string Email { get; set; } = "testadmin@localhost";
    public List<SeedExtraUser> ExtraUsers { get; set; } = new();
}

public class SeedExtraUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class RecurrenceSettings
{
    public int DefaultHorizonMonths { get; set; } = 6;
    public int ExpansionCheckIntervalHours { get; set; } = 24;
}

public class TimezoneSettings
{
    public string DefaultTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
}
