using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Novell.Directory.Ldap;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Services.Auth;

public class LdapAuthService : IAuthenticationService
{
    private readonly AppDbContext _db;
    private readonly ActiveDirectorySettings _adSettings;
    private readonly ILogger<LdapAuthService> _logger;

    public LdapAuthService(AppDbContext db, IOptions<ActiveDirectorySettings> adSettings, ILogger<LdapAuthService> logger)
    {
        _db = db;
        _adSettings = adSettings.Value;
        _logger = logger;
    }

    public async Task<AuthResult> ValidateAsync(string username, string password)
    {
        // First try local account (for seeded test user even when AD is enabled)
        var localUser = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsLocalAccount);
        if (localUser is not null && !string.IsNullOrEmpty(localUser.PasswordHash))
        {
            if (BCrypt.Net.BCrypt.Verify(password, localUser.PasswordHash))
                return AuthResult.Succeeded(localUser);
        }

        // Try LDAP bind
        try
        {
            using var connection = new LdapConnection();
            if (_adSettings.UseSsl)
            {
                connection.SecureSocketLayer = true;
            }

            await connection.ConnectAsync(_adSettings.Host, _adSettings.Port);

            var bindDn = $"{_adSettings.Domain}\\{username}";
            await connection.BindAsync(bindDn, password);

            // Search for user attributes
            string displayName = username;
            string email = $"{username}@{_adSettings.Domain.ToLower()}";

            try
            {
                var searchResults = await connection.SearchAsync(
                    _adSettings.SearchBase,
                    LdapConnection.ScopeSub,
                    $"(sAMAccountName={EscapeLdapFilter(username)})",
                    new[] { "displayName", "mail" },
                    false
                );

                await foreach (var entry in searchResults)
                {
                    var attrs = entry.GetAttributeSet();
                    if (attrs.ContainsKey("displayName"))
                        displayName = attrs["displayName"].StringValue ?? displayName;
                    if (attrs.ContainsKey("mail"))
                        email = attrs["mail"].StringValue ?? email;
                    break; // Only need the first result
                }
            }
            catch (LdapException ex)
            {
                _logger.LogWarning(ex, "Could not query AD attributes for user '{Username}', using defaults.", username);
            }

            // Upsert user in local database
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && !u.IsLocalAccount);
            if (user is null)
            {
                user = new AppUser
                {
                    Username = username,
                    DisplayName = displayName,
                    Email = email,
                    IsLocalAccount = false,
                    CreatedAt = DateTime.UtcNow
                };
                _db.Users.Add(user);
            }
            else
            {
                user.DisplayName = displayName;
                user.Email = email;
            }

            await _db.SaveChangesAsync();
            return AuthResult.Succeeded(user);
        }
        catch (LdapException ex)
        {
            _logger.LogWarning(ex, "LDAP authentication failed for user '{Username}'.", username);
            return AuthResult.Failed("Invalid username or password.");
        }
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
