using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Data;

public class DbSeeder
{
    private readonly AppDbContext _db;
    private readonly SeedUserSettings _seedSettings;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(AppDbContext db, IOptions<SeedUserSettings> seedSettings, ILogger<DbSeeder> logger)
    {
        _db = db;
        _seedSettings = seedSettings.Value;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        await _db.Database.MigrateAsync();

        if (!_seedSettings.Enabled) return;

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.Username == _seedSettings.Username);
        if (existing is not null)
        {
            _logger.LogInformation("Seed user '{Username}' already exists, skipping.", _seedSettings.Username);
            return;
        }

        var user = new AppUser
        {
            Username = _seedSettings.Username,
            DisplayName = _seedSettings.DisplayName,
            Email = _seedSettings.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(_seedSettings.Password),
            IsLocalAccount = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Seeded test user '{Username}'.", _seedSettings.Username);
    }
}
