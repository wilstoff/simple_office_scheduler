using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
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

    /// <summary>
    /// JsonSerializerOptions configured for NodaTime, matching the server config.
    /// </summary>
    protected static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

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
                CreatedAt = SystemClock.Instance.GetCurrentInstant()
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
        LocalDateTime? startTime = null,
        LocalDateTime? endTime = null,
        int capacity = 5,
        string? timeZoneId = null,
        RecurrencePatternDto? recurrence = null)
    {
        var start = startTime ?? LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var end = endTime ?? start.PlusHours(1);

        var request = new CreateEventRequest
        {
            Title = title,
            Description = description,
            StartTime = start,
            EndTime = end,
            Capacity = capacity,
            TimeZoneId = timeZoneId,
            Recurrence = recurrence
        };

        var response = await Client.PostAsJsonAsync("/api/events", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EventResponse>(JsonOptions))!;
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
            CreatedAt = SystemClock.Instance.GetCurrentInstant()
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    protected async Task LoginAsAsync(string username, string password = "Password1!")
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password
        });
        response.EnsureSuccessStatusCode();
    }
}
