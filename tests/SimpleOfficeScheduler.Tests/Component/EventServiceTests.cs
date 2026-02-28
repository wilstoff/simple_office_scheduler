using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodaTime;
using NodaTime.Testing;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Calendar;
using SimpleOfficeScheduler.Services.Events;
using SimpleOfficeScheduler.Services.Recurrence;

namespace SimpleOfficeScheduler.Tests;

public class EventServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly EventService _sut;

    public EventServiceTests()
    {
        // In-memory SQLite with a shared connection so it persists across DbContext calls
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _calendarMock = new Mock<ICalendarInviteService>();
        _calendarMock
            .Setup(c => c.CreateMeetingAsync(It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<AppUser>()))
            .ReturnsAsync(() => "graph-id-" + Guid.NewGuid());

        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var recurrenceSettings = Options.Create(new RecurrenceSettings
        {
            DefaultHorizonMonths = 6,
            ExpansionCheckIntervalHours = 24
        });

        _sut = new EventService(
            _db,
            new RecurrenceExpander(),
            _calendarMock.Object,
            recurrenceSettings,
            _clock,
            NullLogger<EventService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task<AppUser> SeedOwnerAsync()
    {
        var owner = new AppUser
        {
            Username = "owner",
            DisplayName = "Owner",
            Email = "owner@test.local",
            IsLocalAccount = true,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.Add(owner);
        await _db.SaveChangesAsync();
        return owner;
    }

    private async Task<AppUser> SeedUserAsync(string username = "user2")
    {
        var user = new AppUser
        {
            Username = username,
            DisplayName = $"User {username}",
            Email = $"{username}@test.local",
            IsLocalAccount = true,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private Event MakeSingleEvent(int ownerUserId, string title = "Test Event", int capacity = 5)
    {
        return new Event
        {
            Title = title,
            StartTime = new LocalDateTime(2026, 3, 10, 9, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 10, 0),
            Capacity = capacity,
            TimeZoneId = "America/Chicago",
            OwnerUserId = ownerUserId
        };
    }

    private Event MakeWeeklyEvent(int ownerUserId, string title = "Weekly Meeting")
    {
        return new Event
        {
            Title = title,
            StartTime = new LocalDateTime(2026, 3, 10, 9, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 10, 0),
            Capacity = 5,
            TimeZoneId = "America/Chicago",
            OwnerUserId = ownerUserId,
            Recurrence = new RecurrencePattern
            {
                Type = RecurrenceType.Weekly,
                Interval = 1,
                DaysOfWeek = new List<DayOfWeek> { DayOfWeek.Tuesday }
            }
        };
    }

    // ── CreateEventAsync ────────────────────────────────────────────

    [Fact]
    public async Task CreateEvent_SingleEvent_CreatesOneOccurrence()
    {
        var owner = await SeedOwnerAsync();
        var evt = MakeSingleEvent(owner.Id);

        var result = await _sut.CreateEventAsync(evt, owner.Id);

        var occurrences = await _db.EventOccurrences.Where(o => o.EventId == result.Id).ToListAsync();
        Assert.Single(occurrences);
        Assert.Equal(new LocalDateTime(2026, 3, 10, 9, 0), occurrences[0].StartTime);
    }

    [Fact]
    public async Task CreateEvent_WeeklyRecurrence_CreatesMultipleOccurrences()
    {
        var owner = await SeedOwnerAsync();
        var evt = MakeWeeklyEvent(owner.Id);

        var result = await _sut.CreateEventAsync(evt, owner.Id);

        var occurrences = await _db.EventOccurrences
            .Where(o => o.EventId == result.Id)
            .OrderBy(o => o.StartTime)
            .ToListAsync();

        // Should have multiple weekly occurrences within 6 month horizon
        Assert.True(occurrences.Count > 1);
        // All should be Tuesdays
        Assert.All(occurrences, o => Assert.Equal(IsoDayOfWeek.Tuesday, o.StartTime.DayOfWeek));
    }

    [Fact]
    public async Task CreateEvent_SetsOwnerAndTimestamps()
    {
        var owner = await SeedOwnerAsync();
        var evt = MakeSingleEvent(owner.Id);

        var result = await _sut.CreateEventAsync(evt, owner.Id);

        Assert.Equal(owner.Id, result.OwnerUserId);
        Assert.Equal(_clock.GetCurrentInstant(), result.CreatedAt);
        Assert.Equal(_clock.GetCurrentInstant(), result.UpdatedAt);
        Assert.Equal(60, result.DurationMinutes);
    }

    // ── GetEventAsync ───────────────────────────────────────────────

    [Fact]
    public async Task GetEvent_ExistingId_ReturnsEventWithOccurrences()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var result = await _sut.GetEventAsync(evt.Id);

        Assert.NotNull(result);
        Assert.Equal(evt.Id, result.Id);
        Assert.NotNull(result.Owner);
        Assert.Single(result.Occurrences);
    }

    [Fact]
    public async Task GetEvent_NonExistentId_ReturnsNull()
    {
        var result = await _sut.GetEventAsync(999);
        Assert.Null(result);
    }

    // ── SearchEventsAsync ───────────────────────────────────────────

    [Fact]
    public async Task SearchEvents_ByTitle_ReturnsMatching()
    {
        var owner = await SeedOwnerAsync();
        await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, "Alpha Meeting"), owner.Id);
        await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, "Beta Workshop"), owner.Id);

        var results = await _sut.SearchEventsAsync("alpha");

        Assert.Single(results);
        Assert.Equal("Alpha Meeting", results[0].Title);
    }

    [Fact]
    public async Task SearchEvents_NullTerm_ReturnsAll()
    {
        var owner = await SeedOwnerAsync();
        await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, "Event A"), owner.Id);
        await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, "Event B"), owner.Id);

        var results = await _sut.SearchEventsAsync(null);

        Assert.Equal(2, results.Count);
    }

    // ── SignUpAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task SignUp_Success_CreatesSignupAndCalendarMeeting()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id);

        Assert.True(success);
        Assert.Null(error);

        // Signup in DB
        var signup = await _db.EventSignups.FirstOrDefaultAsync(s => s.EventOccurrenceId == occurrenceId && s.UserId == user.Id);
        Assert.NotNull(signup);

        // Calendar meeting created
        _calendarMock.Verify(c => c.CreateMeetingAsync(
            It.Is<EventOccurrence>(o => o.Id == occurrenceId),
            It.Is<AppUser>(u => u.Id == owner.Id),
            It.Is<AppUser>(u => u.Id == user.Id)),
            Times.Once);

        // GraphEventId stored
        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.NotNull(occurrence!.GraphEventId);
    }

    [Fact]
    public async Task SignUp_SecondUser_AddsAttendee()
    {
        var owner = await SeedOwnerAsync();
        var user1 = await SeedUserAsync("user1");
        var user2 = await SeedUserAsync("user2");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // First signup creates meeting
        await _sut.SignUpAsync(occurrenceId, user1.Id);

        // Second signup should add attendee
        var (success, _) = await _sut.SignUpAsync(occurrenceId, user2.Id);

        Assert.True(success);
        _calendarMock.Verify(c => c.AddAttendeeAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == owner.Id),
            It.Is<AppUser>(u => u.Id == user2.Id)),
            Times.Once);
    }

    [Fact]
    public async Task SignUp_Full_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user1 = await SeedUserAsync("user1");
        var user2 = await SeedUserAsync("user2");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, capacity: 1), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, user1.Id);
        var (success, error) = await _sut.SignUpAsync(occurrenceId, user2.Id);

        Assert.False(success);
        Assert.Contains("full", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignUp_Duplicate_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, user.Id);
        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id);

        Assert.False(success);
        Assert.Contains("already", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignUp_CancelledOccurrence_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.CancelOccurrenceAsync(occurrenceId, owner.Id);
        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id);

        Assert.False(success);
        Assert.Contains("cancelled", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── CancelSignUpAsync ───────────────────────────────────────────

    [Fact]
    public async Task CancelSignUp_Success_RemovesSignupAndCallsRemoveAttendee()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, user.Id);
        var (success, error) = await _sut.CancelSignUpAsync(occurrenceId, user.Id);

        Assert.True(success);
        Assert.Null(error);

        // Signup removed from DB
        var signup = await _db.EventSignups.FirstOrDefaultAsync(s => s.EventOccurrenceId == occurrenceId && s.UserId == user.Id);
        Assert.Null(signup);

        // RemoveAttendeeAsync called
        _calendarMock.Verify(c => c.RemoveAttendeeAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == user.Id)),
            Times.Once);
    }

    [Fact]
    public async Task CancelSignUp_NoGraphEventId_SkipsCalendarCall()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Manually add signup without triggering calendar (set GraphEventId to null)
        _db.EventSignups.Add(new EventSignup
        {
            EventOccurrenceId = occurrenceId,
            UserId = user.Id,
            SignedUpAt = _clock.GetCurrentInstant()
        });
        await _db.SaveChangesAsync();

        // Ensure GraphEventId is null
        var occ = await _db.EventOccurrences.FindAsync(occurrenceId);
        occ!.GraphEventId = null;
        await _db.SaveChangesAsync();

        var (success, _) = await _sut.CancelSignUpAsync(occurrenceId, user.Id);

        Assert.True(success);
        _calendarMock.Verify(c => c.RemoveAttendeeAsync(It.IsAny<string>(), It.IsAny<AppUser>()), Times.Never);
    }

    [Fact]
    public async Task CancelSignUp_NotSignedUp_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.CancelSignUpAsync(occurrenceId, user.Id);

        Assert.False(success);
        Assert.Contains("not signed up", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── CancelOccurrenceAsync ───────────────────────────────────────

    [Fact]
    public async Task CancelOccurrence_AsOwner_SetsCancelledAndCancelsMeeting()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Sign up to create a graph event
        await _sut.SignUpAsync(occurrenceId, user.Id);

        var (success, error) = await _sut.CancelOccurrenceAsync(occurrenceId, owner.Id);

        Assert.True(success);
        Assert.Null(error);

        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.True(occurrence!.IsCancelled);

        _calendarMock.Verify(c => c.CancelMeetingAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == owner.Id)),
            Times.Once);
    }

    [Fact]
    public async Task CancelOccurrence_NotOwner_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.CancelOccurrenceAsync(occurrenceId, user.Id);

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelOccurrence_NoGraphEventId_SkipsCalendarCall()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, _) = await _sut.CancelOccurrenceAsync(occurrenceId, owner.Id);

        Assert.True(success);
        _calendarMock.Verify(c => c.CancelMeetingAsync(It.IsAny<string>(), It.IsAny<AppUser>()), Times.Never);
    }

    // ── UncancelOccurrenceAsync ─────────────────────────────────────

    [Fact]
    public async Task UncancelOccurrence_AsOwner_ClearsCancelled()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.CancelOccurrenceAsync(occurrenceId, owner.Id);
        var (success, error) = await _sut.UncancelOccurrenceAsync(occurrenceId, owner.Id);

        Assert.True(success);
        Assert.Null(error);

        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.False(occurrence!.IsCancelled);
    }

    [Fact]
    public async Task UncancelOccurrence_NotCancelled_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.UncancelOccurrenceAsync(occurrenceId, owner.Id);

        Assert.False(success);
        Assert.Contains("not cancelled", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── UpdateEventAsync ────────────────────────────────────────────

    [Fact]
    public async Task UpdateEvent_UpdatesBasicProperties()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, "Original"), owner.Id);

        var updated = new Event
        {
            Id = evt.Id,
            Title = "Updated Title",
            Description = "New description",
            StartTime = new LocalDateTime(2026, 3, 10, 10, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 11, 30),
            Capacity = 10,
            TimeZoneId = "America/Chicago"
        };

        var (success, _) = await _sut.UpdateEventAsync(updated, owner.Id);

        Assert.True(success);

        var fromDb = await _db.Events.FindAsync(evt.Id);
        Assert.Equal("Updated Title", fromDb!.Title);
        Assert.Equal("New description", fromDb.Description);
        Assert.Equal(10, fromDb.Capacity);
        Assert.Equal(90, fromDb.DurationMinutes);
    }

    [Fact]
    public async Task UpdateEvent_RegeneratesOccurrences_KeepsOnesWithSignups()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeWeeklyEvent(owner.Id), owner.Id);

        // Sign up for the first occurrence
        var firstOcc = await _db.EventOccurrences
            .Where(o => o.EventId == evt.Id)
            .OrderBy(o => o.StartTime)
            .FirstAsync();
        await _sut.SignUpAsync(firstOcc.Id, user.Id);

        var occCountBefore = await _db.EventOccurrences.CountAsync(o => o.EventId == evt.Id);

        // Update the event (triggers regeneration)
        var updated = new Event
        {
            Id = evt.Id,
            Title = "Updated Weekly",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity,
            TimeZoneId = evt.TimeZoneId,
            Recurrence = evt.Recurrence
        };

        var (success, _) = await _sut.UpdateEventAsync(updated, owner.Id);
        Assert.True(success);

        // First occurrence (with signup) should still exist
        var keptOcc = await _db.EventOccurrences.FindAsync(firstOcc.Id);
        Assert.NotNull(keptOcc);

        // Should still have signups on it
        var signups = await _db.EventSignups.Where(s => s.EventOccurrenceId == firstOcc.Id).CountAsync();
        Assert.Equal(1, signups);
    }

    [Fact]
    public async Task UpdateEvent_NotOwner_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var updated = new Event
        {
            Id = evt.Id,
            Title = "Hijacked",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity,
            TimeZoneId = evt.TimeZoneId
        };

        var (success, error) = await _sut.UpdateEventAsync(updated, user.Id);

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── TransferOwnershipAsync ──────────────────────────────────────

    [Fact]
    public async Task TransferOwnership_Success_ChangesOwner()
    {
        var owner = await SeedOwnerAsync();
        var newOwner = await SeedUserAsync("newowner");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var (success, error) = await _sut.TransferOwnershipAsync(evt.Id, owner.Id, newOwner.Id);

        Assert.True(success);
        Assert.Null(error);

        var fromDb = await _db.Events.FindAsync(evt.Id);
        Assert.Equal(newOwner.Id, fromDb!.OwnerUserId);
    }

    [Fact]
    public async Task TransferOwnership_NotOwner_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var newOwner = await SeedUserAsync("newowner");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var (success, error) = await _sut.TransferOwnershipAsync(evt.Id, user.Id, newOwner.Id);

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── DeleteEventAsync ────────────────────────────────────────────

    [Fact]
    public async Task DeleteEvent_CancelsAllMeetingsAndDeletes()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Sign up to create a graph event
        await _sut.SignUpAsync(occurrenceId, user.Id);

        var (success, error) = await _sut.DeleteEventAsync(evt.Id, owner.Id);

        Assert.True(success);
        Assert.Null(error);

        // Event deleted from DB
        var fromDb = await _db.Events.FindAsync(evt.Id);
        Assert.Null(fromDb);

        // CancelMeetingAsync called
        _calendarMock.Verify(c => c.CancelMeetingAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == owner.Id)),
            Times.Once);
    }

    [Fact]
    public async Task DeleteEvent_NotOwner_ReturnsFalse()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var (success, error) = await _sut.DeleteEventAsync(evt.Id, user.Id);

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteEvent_NonExistent_ReturnsFalse()
    {
        var (success, error) = await _sut.DeleteEventAsync(999, 1);

        Assert.False(success);
        Assert.Contains("not found", error!, StringComparison.OrdinalIgnoreCase);
    }
}
