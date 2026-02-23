namespace SimpleOfficeScheduler.Services.Users;

public interface IUserSearchService
{
    Task<List<UserSearchResult>> SearchUsersAsync(string searchTerm, int? excludeUserId = null, int maxResults = 10);

    /// <summary>
    /// Ensures an AD-only user exists in the local database by looking them up in AD
    /// and creating a local record. Returns the user with their local DB ID.
    /// </summary>
    Task<UserSearchResult?> EnsureUserAsync(string username);
}

public class UserSearchResult
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdOnly { get; set; }
}
