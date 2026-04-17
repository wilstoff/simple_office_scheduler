using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Data;

public class DbSeeder
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SeedUserSettings _seedSettings;
    private readonly IClock _clock;
    private readonly ILogger<DbSeeder> _logger;

    public DbSeeder(IDbContextFactory<AppDbContext> dbFactory, IOptions<SeedUserSettings> seedSettings, IClock clock, ILogger<DbSeeder> logger)
    {
        _dbFactory = dbFactory;
        _seedSettings = seedSettings.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task SeedAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        if (!_seedSettings.Enabled) return;

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == _seedSettings.Username);
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

            db.Users.Add(user);
            await db.SaveChangesAsync();

            _logger.LogInformation("Seeded test user '{Username}'.", _seedSettings.Username);
        }

        foreach (var extra in _seedSettings.ExtraUsers)
        {
            if (string.IsNullOrEmpty(extra.Username)) continue;
            if (await db.Users.AnyAsync(u => u.Username == extra.Username))
            {
                _logger.LogInformation("Extra seed user '{Username}' already exists, skipping.", extra.Username);
                continue;
            }

            db.Users.Add(new AppUser
            {
                Username = extra.Username,
                DisplayName = extra.DisplayName,
                Email = extra.Email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(extra.Password),
                IsLocalAccount = true,
                CreatedAt = _clock.GetCurrentInstant()
            });
            await db.SaveChangesAsync();
            _logger.LogInformation("Seeded extra user '{Username}'.", extra.Username);
        }
    }
}
