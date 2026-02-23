using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Users;

public class UserSearchService : IUserSearchService
{
    private readonly AppDbContext _db;
    private readonly ActiveDirectorySettings _adSettings;
    private readonly ILogger<UserSearchService> _logger;

    public UserSearchService(
        AppDbContext db,
        IOptions<ActiveDirectorySettings> adSettings,
        ILogger<UserSearchService> logger)
    {
        _db = db;
        _adSettings = adSettings.Value;
        _logger = logger;
    }

    public async Task<List<UserSearchResult>> SearchUsersAsync(
        string searchTerm, int? excludeUserId = null, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Length < 2)
            return new List<UserSearchResult>();

        var term = searchTerm.Trim();

        // Search local database
        var dbQuery = _db.Users
            .Where(u => EF.Functions.Like(u.DisplayName, $"%{term}%")
                     || EF.Functions.Like(u.Username, $"%{term}%")
                     || EF.Functions.Like(u.Email, $"%{term}%"));

        if (excludeUserId.HasValue)
            dbQuery = dbQuery.Where(u => u.Id != excludeUserId.Value);

        var dbResults = await dbQuery
            .OrderBy(u => u.DisplayName)
            .Take(maxResults)
            .Select(u => new UserSearchResult
            {
                Id = u.Id,
                DisplayName = u.DisplayName,
                Username = u.Username,
                Email = u.Email,
                IsAdOnly = false
            })
            .ToListAsync();

        // Search Active Directory if enabled and service account is configured
        if (_adSettings.Enabled
            && !string.IsNullOrEmpty(_adSettings.ServiceAccountDn)
            && !string.IsNullOrEmpty(_adSettings.ServiceAccountPassword))
        {
            var adResults = await SearchAdAsync(term, maxResults);
            var localUsernames = new HashSet<string>(
                dbResults.Select(r => r.Username),
                StringComparer.OrdinalIgnoreCase);

            foreach (var adUser in adResults)
            {
                if (localUsernames.Contains(adUser.Username))
                    continue; // Already in local results

                // Check if this AD user is excluded
                if (excludeUserId.HasValue)
                {
                    var localMatch = await _db.Users
                        .FirstOrDefaultAsync(u => u.Username == adUser.Username);
                    if (localMatch?.Id == excludeUserId.Value)
                        continue;
                }

                adUser.IsAdOnly = true;
                dbResults.Add(adUser);

                if (dbResults.Count >= maxResults)
                    break;
            }
        }

        return dbResults.Take(maxResults).ToList();
    }

    public async Task<UserSearchResult?> EnsureUserAsync(string username)
    {
        // Check if already in local DB
        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (existing is not null)
        {
            return new UserSearchResult
            {
                Id = existing.Id,
                DisplayName = existing.DisplayName,
                Username = existing.Username,
                Email = existing.Email,
                IsAdOnly = false
            };
        }

        // Must have AD enabled to look up and create the user
        if (!_adSettings.Enabled
            || string.IsNullOrEmpty(_adSettings.ServiceAccountDn)
            || string.IsNullOrEmpty(_adSettings.ServiceAccountPassword))
        {
            return null;
        }

        // Look up in AD and create local record
        try
        {
            using var connection = new LdapConnection();
            if (_adSettings.UseSsl)
                connection.SecureSocketLayer = true;

            await connection.ConnectAsync(_adSettings.Host, _adSettings.Port);
            await connection.BindAsync(_adSettings.ServiceAccountDn, _adSettings.ServiceAccountPassword);

            var filter = $"(sAMAccountName={EscapeLdapFilter(username)})";
            var searchResults = await connection.SearchAsync(
                _adSettings.SearchBase,
                LdapConnection.ScopeSub,
                filter,
                new[] { "displayName", "mail", "sAMAccountName" },
                false);

            await foreach (var entry in searchResults)
            {
                var attrs = entry.GetAttributeSet();
                var displayName = attrs.ContainsKey("displayName")
                    ? attrs["displayName"].StringValue ?? username
                    : username;
                var email = attrs.ContainsKey("mail")
                    ? attrs["mail"].StringValue ?? $"{username}@{_adSettings.Domain.ToLower()}"
                    : $"{username}@{_adSettings.Domain.ToLower()}";

                var user = new AppUser
                {
                    Username = username,
                    DisplayName = displayName,
                    Email = email,
                    IsLocalAccount = false,
                    CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant()
                };
                _db.Users.Add(user);
                await _db.SaveChangesAsync();

                return new UserSearchResult
                {
                    Id = user.Id,
                    DisplayName = user.DisplayName,
                    Username = user.Username,
                    Email = user.Email,
                    IsAdOnly = false
                };
            }
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "Failed to look up AD user '{Username}' for ensure.", username);
        }

        return null;
    }

    private async Task<List<UserSearchResult>> SearchAdAsync(string term, int maxResults)
    {
        var results = new List<UserSearchResult>();

        try
        {
            using var connection = new LdapConnection();
            if (_adSettings.UseSsl)
                connection.SecureSocketLayer = true;

            await connection.ConnectAsync(_adSettings.Host, _adSettings.Port);
            await connection.BindAsync(_adSettings.ServiceAccountDn, _adSettings.ServiceAccountPassword);

            var escapedTerm = EscapeLdapFilter(term);
            var filter = $"(&(objectClass=user)(|(displayName=*{escapedTerm}*)(sAMAccountName=*{escapedTerm}*)(mail=*{escapedTerm}*)))";

            var searchResults = await connection.SearchAsync(
                _adSettings.SearchBase,
                LdapConnection.ScopeSub,
                filter,
                new[] { "displayName", "mail", "sAMAccountName" },
                false);

            await foreach (var entry in searchResults)
            {
                var attrs = entry.GetAttributeSet();
                var username = attrs.ContainsKey("sAMAccountName")
                    ? attrs["sAMAccountName"].StringValue ?? ""
                    : "";

                if (string.IsNullOrEmpty(username))
                    continue;

                var displayName = attrs.ContainsKey("displayName")
                    ? attrs["displayName"].StringValue ?? username
                    : username;
                var email = attrs.ContainsKey("mail")
                    ? attrs["mail"].StringValue ?? $"{username}@{_adSettings.Domain.ToLower()}"
                    : $"{username}@{_adSettings.Domain.ToLower()}";

                results.Add(new UserSearchResult
                {
                    Id = 0,
                    DisplayName = displayName,
                    Username = username,
                    Email = email,
                    IsAdOnly = true
                });

                if (results.Count >= maxResults)
                    break;
            }
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "AD search failed for term '{Term}'.", term);
        }

        return results;
    }

    private static string EscapeLdapFilter(string input)
    {
        return input
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");
    }
}
