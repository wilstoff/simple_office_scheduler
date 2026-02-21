using Microsoft.EntityFrameworkCore;
using SimpleOfficeScheduler.Data;

namespace SimpleOfficeScheduler.Services.Auth;

public class LocalAuthService : IAuthenticationService
{
    private readonly AppDbContext _db;
    private readonly ILogger<LocalAuthService> _logger;

    public LocalAuthService(AppDbContext db, ILogger<LocalAuthService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AuthResult> ValidateAsync(string username, string password)
    {
        _logger.LogInformation("Login attempt for user '{Username}'", username);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsLocalAccount);
        if (user is null)
        {
            _logger.LogWarning("User '{Username}' not found or not a local account.", username);
            return AuthResult.Failed("Invalid username or password.");
        }

        if (string.IsNullOrEmpty(user.PasswordHash))
        {
            _logger.LogWarning("User '{Username}' has no password hash.", username);
            return AuthResult.Failed("Invalid username or password.");
        }

        _logger.LogInformation("Found user '{Username}', verifying password (hash length: {Len})...", username, user.PasswordHash.Length);

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            _logger.LogWarning("Password verification failed for user '{Username}'.", username);
            return AuthResult.Failed("Invalid username or password.");
        }

        _logger.LogInformation("Login successful for user '{Username}'.", username);
        return AuthResult.Succeeded(user);
    }
}
