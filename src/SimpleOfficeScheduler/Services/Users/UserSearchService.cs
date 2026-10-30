using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Ldap;
using ILdapConnection = SimpleOfficeScheduler.Services.Ldap.ILdapConnection;

namespace SimpleOfficeScheduler.Services.Users;

public class UserSearchService : IUserSearchService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ActiveDirectorySettings _adSettings;
    private readonly ILdapConnectionFactory _ldapFactory;
    private readonly ILogger<UserSearchService> _logger;

    public UserSearchService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOptions<ActiveDirectorySettings> adSettings,
        ILdapConnectionFactory ldapFactory,
        ILogger<UserSearchService> logger)
    {
        _dbFactory = dbFactory;
        _adSettings = adSettings.Value;
        _ldapFactory = ldapFactory;
        _logger = logger;
    }

    public async Task<List<UserSearchResult>> SearchUsersAsync(
        string searchTerm, int? excludeUserId = null, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(searchTerm) || searchTerm.Length < 2)
            return new List<UserSearchResult>();

        var term = searchTerm.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync();

        // Search local database
        var dbQuery = db.Users
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
        if (IsAdConfigured)
        {
            var adResults = await SearchAdAsync(term, maxResults);
            var localUsernames = new HashSet<string>(
                dbResults.Select(r => r.Username),
                StringComparer.OrdinalIgnoreCase);

            foreach (var adUser in adResults)
            {
                if (localUsernames.Contains(adUser.Username))
                {
                    // Already in local results, but the local row may be a stale copy of AD
                    var local = dbResults.First(r =>
                        string.Equals(r.Username, adUser.Username, StringComparison.OrdinalIgnoreCase));
                    await RefreshFromAdAsync(db, local, adUser);
                    continue;
                }

                // Check if this AD user is excluded
                if (excludeUserId.HasValue)
                {
                    var localMatch = await db.Users
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
        await using var db = await _dbFactory.CreateDbContextAsync();

        // Check if already in local DB. A local account is not an AD user, so it is final.
        var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (existing is not null && (existing.IsLocalAccount || !IsAdConfigured))
            return ToResult(existing);

        // Must have AD enabled to look up and create the user
        if (!IsAdConfigured)
            return null;

        // An AD user's row is only a copy of AD, so look them up even when the row exists. Returning
        // the stored row unchanged kept a bad address (one saved as "jdoe@corp") forever.
        var adUser = await LookupAdUserAsync(username);
        if (adUser is null)
            return existing is null ? null : ToResult(existing);

        if (existing is null)
        {
            existing = new AppUser
            {
                Username = username,
                IsLocalAccount = false,
                CreatedAt = NodaTime.SystemClock.Instance.GetCurrentInstant()
            };
            db.Users.Add(existing);
        }
        existing.DisplayName = adUser.DisplayName;
        existing.Email = adUser.Email;
        await db.SaveChangesAsync();

        return ToResult(existing);
    }

    private bool IsAdConfigured =>
        _adSettings.Enabled
        && !string.IsNullOrEmpty(_adSettings.ServiceAccountUsername)
        && !string.IsNullOrEmpty(_adSettings.ServiceAccountPassword);

    private static UserSearchResult ToResult(AppUser user) => new()
    {
        Id = user.Id,
        DisplayName = user.DisplayName,
        Username = user.Username,
        Email = user.Email,
        IsAdOnly = false
    };

    /// <summary>
    /// Brings a local search result and its row in line with AD. Local accounts are not AD users and
    /// are left alone.
    /// </summary>
    private static async Task RefreshFromAdAsync(AppDbContext db, UserSearchResult local, UserSearchResult adUser)
    {
        var row = await db.Users.FirstOrDefaultAsync(u => u.Id == local.Id && !u.IsLocalAccount);
        if (row is null)
            return;

        row.DisplayName = adUser.DisplayName;
        row.Email = adUser.Email;
        await db.SaveChangesAsync();

        local.DisplayName = row.DisplayName;
        local.Email = row.Email;
    }

    private async Task<ILdapConnection> OpenServiceConnectionAsync()
    {
        var connection = _ldapFactory.Create();
        try
        {
            if (_adSettings.UseSsl)
                connection.SecureSocketLayer = true;

            await connection.ConnectAsync(_adSettings.Host, _adSettings.Port);
            var bindDn = $"{_adSettings.Domain}\\{_adSettings.ServiceAccountUsername}";
            await connection.BindAsync(bindDn, _adSettings.ServiceAccountPassword!);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>The AD entry for an exact sAMAccountName, or null if there is none or AD fails.</summary>
    private async Task<UserSearchResult?> LookupAdUserAsync(string username)
    {
        try
        {
            using var connection = await OpenServiceConnectionAsync();

            var filter = $"(sAMAccountName={EscapeLdapFilter(username)})";
            var searchResults = connection.SearchAsync(
                _adSettings.SearchBase,
                LdapConnection.ScopeSub,
                filter,
                AdUserAttributes.Requested,
                false);

            await foreach (var entry in searchResults)
                return ToAdResult(entry.GetAttributeSet(), username);
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
            using var connection = await OpenServiceConnectionAsync();

            var escapedTerm = EscapeLdapFilter(term);
            var filter = $"(&(objectClass=user)(|(displayName=*{escapedTerm}*)(sAMAccountName=*{escapedTerm}*)(mail=*{escapedTerm}*)))";

            var searchResults = connection.SearchAsync(
                _adSettings.SearchBase,
                LdapConnection.ScopeSub,
                filter,
                AdUserAttributes.Requested,
                false);

            await foreach (var entry in searchResults)
            {
                var attrs = entry.GetAttributeSet();
                var username = AdUserAttributes.Get(attrs, "sAMAccountName");
                if (username is null)
                    continue;

                results.Add(ToAdResult(attrs, username));

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

    private UserSearchResult ToAdResult(LdapAttributeSet attrs, string username) => new()
    {
        Id = 0,
        DisplayName = AdUserAttributes.Get(attrs, "displayName") ?? username,
        Username = username,
        Email = AdUserAttributes.ResolveEmail(attrs, username, _adSettings),
        IsAdOnly = true
    };

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
