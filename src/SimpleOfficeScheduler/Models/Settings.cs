namespace SimpleOfficeScheduler.Models;

public class ActiveDirectorySettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 389;
    public bool UseSsl { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string SearchBase { get; set; } = string.Empty;
}

public class GraphApiSettings
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}

public class SeedUserSettings
{
    public bool Enabled { get; set; }
    public string Username { get; set; } = "testadmin";
    public string Password { get; set; } = "Test123!";
    public string DisplayName { get; set; } = "Test Admin";
    public string Email { get; set; } = "testadmin@localhost";
}

public class RecurrenceSettings
{
    public int DefaultHorizonMonths { get; set; } = 6;
    public int ExpansionCheckIntervalHours { get; set; } = 24;
}
