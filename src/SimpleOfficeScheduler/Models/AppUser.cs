namespace SimpleOfficeScheduler.Models;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public bool IsLocalAccount { get; set; }
    public DateTime CreatedAt { get; set; }

    public ICollection<Event> OwnedEvents { get; set; } = new List<Event>();
    public ICollection<EventSignup> Signups { get; set; } = new List<EventSignup>();
}
