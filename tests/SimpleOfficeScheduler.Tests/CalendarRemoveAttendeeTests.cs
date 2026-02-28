using System.Net;
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
using SimpleOfficeScheduler.Services.Calendar;

namespace SimpleOfficeScheduler.Tests;

public class CalendarRemoveAttendeeTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly SpyCalendarService _spy = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        .ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);

    public CalendarRemoveAttendeeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"scheduler_test_{Guid.NewGuid():N}.db");

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor != null) services.Remove(descriptor);

                    services.AddDbContext<AppDbContext>(options =>
                        options.UseSqlite($"Data Source={_dbPath}",
                            o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

                    var bgService = services.SingleOrDefault(
                        d => d.ImplementationType?.Name == "RecurrenceExpansionBackgroundService");
                    if (bgService != null) services.Remove(bgService);

                    // Replace calendar service with spy
                    var calDescriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(ICalendarInviteService));
                    if (calDescriptor != null) services.Remove(calDescriptor);
                    services.AddSingleton<ICalendarInviteService>(_spy);
                });
            });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        if (!await db.Users.AnyAsync(u => u.Username == "testadmin"))
        {
            db.Users.Add(new AppUser
            {
                Username = "testadmin",
                DisplayName = "Test Admin",
                Email = "testadmin@test.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test123!"),
                IsLocalAccount = true,
                CreatedAt = SystemClock.Instance.GetCurrentInstant()
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CancelSignUp_Calls_RemoveAttendeeAsync()
    {
        // Login
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "testadmin",
            password = "Test123!"
        });
        loginResponse.EnsureSuccessStatusCode();

        // Create event
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var createResponse = await _client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = "Remove Attendee Test",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 5
        }, JsonOptions);
        createResponse.EnsureSuccessStatusCode();
        var evt = await createResponse.Content.ReadFromJsonAsync<EventResponse>(JsonOptions);
        var occurrenceId = evt!.Occurrences.First().Id;

        // Sign up (triggers CreateMeetingAsync → stores GraphEventId)
        var signupResponse = await _client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.OK, signupResponse.StatusCode);

        // Cancel signup
        var cancelResponse = await _client.DeleteAsync($"/api/events/{evt.Id}/signup/{occurrenceId}");
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);

        // Verify RemoveAttendeeAsync was called with the correct email
        Assert.Single(_spy.RemovedAttendees);
        Assert.Equal("testadmin@test.local", _spy.RemovedAttendees[0].Email);
    }

    private class SpyCalendarService : ICalendarInviteService
    {
        public List<(string GraphEventId, string Email)> RemovedAttendees { get; } = new();

        public Task<string> CreateMeetingAsync(EventOccurrence occurrence, AppUser owner, AppUser signee)
            => Task.FromResult("spy-graph-id-" + Guid.NewGuid());

        public Task AddAttendeeAsync(string graphEventId, AppUser owner, AppUser newSignee)
            => Task.CompletedTask;

        public Task RemoveAttendeeAsync(string graphEventId, AppUser attendeeToRemove)
        {
            RemovedAttendees.Add((graphEventId, attendeeToRemove.Email));
            return Task.CompletedTask;
        }

        public Task CancelMeetingAsync(string graphEventId, AppUser owner)
            => Task.CompletedTask;
    }
}
