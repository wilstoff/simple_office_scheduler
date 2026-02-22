using NodaTime;

namespace SimpleOfficeScheduler.Models;

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class CreateEventRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public LocalDateTime StartTime { get; set; }
    public LocalDateTime EndTime { get; set; }
    public int Capacity { get; set; } = 1;
    public string? TimeZoneId { get; set; }
    public RecurrencePatternDto? Recurrence { get; set; }
}

public class UpdateEventRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public LocalDateTime StartTime { get; set; }
    public LocalDateTime EndTime { get; set; }
    public int Capacity { get; set; } = 1;
    public string? TimeZoneId { get; set; }
    public RecurrencePatternDto? Recurrence { get; set; }
}

public class RecurrencePatternDto
{
    public RecurrenceType Type { get; set; }
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public int Interval { get; set; } = 1;
    public LocalDate? RecurrenceEndDate { get; set; }
    public int? MaxOccurrences { get; set; }
}

public class EventResponse
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int OwnerUserId { get; set; }
    public string OwnerDisplayName { get; set; } = string.Empty;
    public LocalDateTime StartTime { get; set; }
    public LocalDateTime EndTime { get; set; }
    public int Capacity { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public RecurrencePatternDto? Recurrence { get; set; }
    public List<OccurrenceResponse> Occurrences { get; set; } = new();
}

public class OccurrenceResponse
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public LocalDateTime StartTime { get; set; }
    public LocalDateTime EndTime { get; set; }
    public Instant StartTimeUtc { get; set; }
    public Instant EndTimeUtc { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public bool IsCancelled { get; set; }
    public int SignupCount { get; set; }
    public List<SignupResponse> Signups { get; set; } = new();
}

public class SignupResponse
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public Instant SignedUpAt { get; set; }
    public string? Message { get; set; }
}

public class SignUpRequest
{
    public string? Message { get; set; }
}

public class UserResponse
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string ThemePreference { get; set; } = "dark";
    public string? TimeZonePreference { get; set; }
}

public class UserSettingsResponse
{
    public string ThemePreference { get; set; } = "dark";
    public string? TimeZonePreference { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsLocalAccount { get; set; }
}

public class UpdateThemeRequest
{
    public string Theme { get; set; } = "dark";
}

public class UpdateTimezoneRequest
{
    public string TimeZoneId { get; set; } = string.Empty;
}
