using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class IntegrationTestBase : IAsyncLifetime
{
    private readonly string _dbPath;
    protected readonly WebApplicationFactory<Program> Factory;
    protected readonly HttpClient Client;

    protected const string TestUsername = "testadmin";
    protected const string TestPassword = "Test123!";

    public IntegrationTestBase()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"scheduler_test_{Guid.NewGuid():N}.db");

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    // Remove existing DbContext registration
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor != null)
                        services.Remove(descriptor);

                    // Use a unique SQLite DB per test class
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseSqlite($"Data Source={_dbPath}"));

                    // Remove the background service to avoid interference
                    var bgService = services.SingleOrDefault(
                        d => d.ImplementationType?.Name == "RecurrenceExpansionBackgroundService");
                    if (bgService != null)
                        services.Remove(bgService);
                });
            });

        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    public async Task InitializeAsync()
    {
        // Ensure DB is created and seeded
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Seed test user
        if (!await db.Users.AnyAsync(u => u.Username == TestUsername))
        {
            db.Users.Add(new AppUser
            {
                Username = TestUsername,
                DisplayName = "Test Admin",
                Email = "testadmin@test.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(TestPassword),
                IsLocalAccount = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }

    protected async Task LoginAsync(string? username = null, string? password = null)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username = username ?? TestUsername,
            password = password ?? TestPassword
        });
        response.EnsureSuccessStatusCode();
    }

    protected async Task<EventResponse> CreateEventAsync(
        string title = "Test Event",
        string? description = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int capacity = 5,
        RecurrencePatternDto? recurrence = null)
    {
        var start = startTime ?? DateTime.UtcNow.AddDays(1);
        var end = endTime ?? start.AddHours(1);

        var response = await Client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = title,
            Description = description,
            StartTime = start,
            EndTime = end,
            Capacity = capacity,
            Recurrence = recurrence
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventResponse>())!;
    }

    protected async Task<AppUser> CreateSecondUserAsync(string username = "user2")
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var existing = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
        if (existing != null) return existing;

        var user = new AppUser
        {
            Username = username,
            DisplayName = $"User {username}",
            Email = $"{username}@test.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!"),
            IsLocalAccount = true,
            CreatedAt = DateTime.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    protected async Task LoginAsAsync(string username, string password = "Password1!")
    {
        // Need a fresh handler to reset cookies
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password
        });
        response.EnsureSuccessStatusCode();
    }
}
