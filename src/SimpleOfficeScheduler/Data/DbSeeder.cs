using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Data;

public class DbSeeder
{
    private readonly AppDbContext _db;
    private readonly SeedUserSettings _seedSettings;
    private readonly IClock _clock;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(AppDbContext db, IOptions<SeedUserSettings> seedSettings, IClock clock, ILogger<DbSeeder> logger)
    {
        _db = db;
        _seedSettings = seedSettings.Value;
        _clock = clock;
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
        }
        else
        {
            var user = new AppUser
            {
                Username = _seedSettings.Username,
                DisplayName = _seedSettings.DisplayName,
                Email = _seedSettings.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(_seedSettings.Password),
                IsLocalAccount = true,
                CreatedAt = _clock.GetCurrentInstant()
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Seeded test user '{Username}'.", _seedSettings.Username);
        }

        foreach (var extra in _seedSettings.ExtraUsers)
        {
            if (string.IsNullOrEmpty(extra.Username)) continue;
            if (await _db.Users.AnyAsync(u => u.Username == extra.Username))
            {
                _logger.LogInformation("Extra seed user '{Username}' already exists, skipping.", extra.Username);
                continue;
            }

            _db.Users.Add(new AppUser
            {
                Username = extra.Username,
                DisplayName = extra.DisplayName,
                Email = extra.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(extra.Password),
                IsLocalAccount = true,
                CreatedAt = _clock.GetCurrentInstant()
            });
            await _db.SaveChangesAsync();
            _logger.LogInformation("Seeded extra user '{Username}'.", extra.Username);
        }
    }
}
