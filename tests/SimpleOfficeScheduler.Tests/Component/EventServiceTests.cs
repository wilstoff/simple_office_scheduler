using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NodaTime;
using NodaTime.Testing;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services;
using SimpleOfficeScheduler.Services.Calendar;
using SimpleOfficeScheduler.Services.Events;
using SimpleOfficeScheduler.Services.Recurrence;
using SimpleOfficeScheduler.Services.Rooms;

namespace SimpleOfficeScheduler.Tests;

public class EventServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly CalendarUpdateNotifier _notifier;
    private readonly EventService _sut;

    public EventServiceTests()
    {
        // In-memory SQLite with a shared connection so it persists across DbContext calls
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbFactory = new TestDbContextFactory(_options);
        _db = _dbFactory.CreateDbContext();
        _db.Database.EnsureCreated();

        _calendarMock = new Mock<ICalendarInviteService>();
        _calendarMock
            .Setup(c => c.CreateMeetingAsync(It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>()))
            .ReturnsAsync(() => "graph-id-" + Guid.NewGuid());

        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));
        _notifier = new CalendarUpdateNotifier();

        var recurrenceSettings = Options.Create(new RecurrenceSettings
        {
            DefaultHorizonMonths = 6,
            ExpansionCheckIntervalHours = 24
        });

        _sut = new EventService(
            _dbFactory,
            new RecurrenceExpander(),
            _calendarMock.Object,
            new ConfigRoomService(Options.Create(new GraphApiSettings()), NullLogger<ConfigRoomService>.Instance),
            recurrenceSettings,
            Options.Create(new GraphApiSettings()),
            _clock,
            NullLogger<EventService>.Instance,
            _notifier);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Yields a fresh AppDbContext for test reads, so each assertion sees current DB state
    /// rather than the test-seed context's tracker cache after _sut writes via its own contexts.
    /// </summary>
    private AppDbContext NewDb() => _dbFactory.CreateDbContext();

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
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

    private Event MakeSingleEvent(int ownerUserId, string title = "Test Event", int capacity = 5,
        EventType eventType = EventType.OfficeHours)
    {
        return new Event
        {
            Title = title,
            StartTime = new LocalDateTime(2026, 3, 10, 9, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 10, 0),
            Capacity = capacity,
            TimeZoneId = "America/Chicago",
            OwnerUserId = ownerUserId,
            EventType = eventType
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

    [Fact]
    public async Task CreateEvent_EndTimeBeforeStartTime_ThrowsArgumentException()
    {
        var owner = await SeedOwnerAsync();
        var evt = new Event
        {
            Title = "Bad Times",
            StartTime = new LocalDateTime(2026, 3, 10, 10, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 9, 0),
            Capacity = 5,
            TimeZoneId = "America/Chicago"
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateEventAsync(evt, owner.Id));
        Assert.Contains("end time", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateEvent_EndTimeEqualsStartTime_ThrowsArgumentException()
    {
        var owner = await SeedOwnerAsync();
        var evt = new Event
        {
            Title = "Zero Duration",
            StartTime = new LocalDateTime(2026, 3, 10, 10, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 10, 0),
            Capacity = 5,
            TimeZoneId = "America/Chicago"
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateEventAsync(evt, owner.Id));
        Assert.Contains("end time", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");

        Assert.True(success);
        Assert.Null(error);

        // Signup in DB
        var signup = await _db.EventSignups.FirstOrDefaultAsync(s => s.EventOccurrenceId == occurrenceId && s.UserId == user.Id);
        Assert.NotNull(signup);

        // Calendar meeting created
        _calendarMock.Verify(c => c.CreateMeetingAsync(
            It.Is<EventOccurrence>(o => o.Id == occurrenceId),
            It.Is<AppUser>(u => u.Id == owner.Id),
            It.Is<AppUser>(u => u.Id == user.Id),
            It.IsAny<IReadOnlyList<EventSignup>>()),
            Times.Once);

        // GraphEventId stored
        _db.ChangeTracker.Clear();
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
        await _sut.SignUpAsync(occurrenceId, user1.Id, "Topic from user1");

        // Second signup should add attendee
        var (success, _) = await _sut.SignUpAsync(occurrenceId, user2.Id, "Topic from user2");

        Assert.True(success);
        _calendarMock.Verify(c => c.AddAttendeeAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == owner.Id),
            It.Is<AppUser>(u => u.Id == user2.Id),
            It.IsAny<IReadOnlyList<EventSignup>>()),
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

        await _sut.SignUpAsync(occurrenceId, user1.Id, "Topic from user1");
        var (success, error) = await _sut.SignUpAsync(occurrenceId, user2.Id, "Topic from user2");

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

        await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");
        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");

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
        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");

        Assert.False(success);
        Assert.Contains("cancelled", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── CancelSignUpAsync ───────────────────────────────────────────

    [Fact]
    public async Task CancelSignUp_Success_RemovesSignupAndCallsRemoveAttendee()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var user2 = new AppUser { Username = "other", DisplayName = "Other", Email = "other@test.local", PasswordHash = "x", CreatedAt = _clock.GetCurrentInstant() };
        _db.Users.Add(user2);
        await _db.SaveChangesAsync();

        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id, capacity: 5), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Two signups so cancelling one is NOT the last
        await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");
        await _sut.SignUpAsync(occurrenceId, user2.Id, "Topic from user2");
        var (success, error) = await _sut.CancelSignUpAsync(occurrenceId, user.Id);

        Assert.True(success);
        Assert.Null(error);

        // Signup removed from DB
        var signup = await _db.EventSignups.FirstOrDefaultAsync(s => s.EventOccurrenceId == occurrenceId && s.UserId == user.Id);
        Assert.Null(signup);

        // RemoveAttendeeAsync called (not CancelMeetingAsync, since user2 still signed up)
        _calendarMock.Verify(c => c.RemoveAttendeeAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == user.Id),
            It.IsAny<IReadOnlyList<EventSignup>>()),
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
        _calendarMock.Verify(c => c.RemoveAttendeeAsync(It.IsAny<string>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>()), Times.Never);
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

    [Fact]
    public async Task CancelSignUp_LastSignup_CancelsMeetingInsteadOfRemovingAttendee()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Sign up user (creates calendar meeting)
        await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");
        _db.ChangeTracker.Clear();
        var occ = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.NotNull(occ!.GraphEventId);
        var graphEventId = occ.GraphEventId;

        // Cancel the only signup — should cancel the meeting, not just remove attendee
        var (success, error) = await _sut.CancelSignUpAsync(occurrenceId, user.Id);

        Assert.True(success);
        Assert.Null(error);

        // CancelMeetingAsync should be called (not RemoveAttendeeAsync)
        _calendarMock.Verify(c => c.CancelMeetingAsync(graphEventId!, It.IsAny<AppUser>()), Times.Once);
        _calendarMock.Verify(c => c.RemoveAttendeeAsync(It.IsAny<string>(), It.Is<AppUser>(u => u.Id == user.Id), It.IsAny<IReadOnlyList<EventSignup>>()), Times.Never);

        // GraphEventId should be cleared
        _db.ChangeTracker.Clear();
        var refreshed = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.Null(refreshed!.GraphEventId);
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
        await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");

        var (success, error) = await _sut.CancelOccurrenceAsync(occurrenceId, owner.Id);

        Assert.True(success);
        Assert.Null(error);

        _db.ChangeTracker.Clear();
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

    [Fact]
    public async Task CancelThenUncancel_SignUp_CreatesNewMeeting()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // First signup creates a meeting
        await _sut.SignUpAsync(occId, user.Id, "Test topic");
        _db.ChangeTracker.Clear();
        var occ = await _db.EventOccurrences.FindAsync(occId);
        Assert.NotNull(occ!.GraphEventId);

        // Cancel occurrence → meeting cancelled, GraphEventId should be cleared
        await _sut.CancelOccurrenceAsync(occId, owner.Id);
        _db.ChangeTracker.Clear();
        occ = await _db.EventOccurrences.FindAsync(occId);
        Assert.Null(occ!.GraphEventId);

        // Uncancel + re-signup → should create a fresh meeting (2nd CreateMeeting)
        await _sut.UncancelOccurrenceAsync(occId, owner.Id);
        // Cancel signup (last signup) → CancelMeetingAsync + clears GraphEventId
        await _sut.CancelSignUpAsync(occId, user.Id);
        // Re-signup with no GraphEventId → creates 3rd meeting
        await _sut.SignUpAsync(occId, user.Id, "Test topic");

        _calendarMock.Verify(c => c.CreateMeetingAsync(
            It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>()),
            Times.Exactly(3));
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

    [Fact]
    public async Task UncancelOccurrence_WithSignups_RecreatesCalendarMeeting()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Signup creates a meeting, then cancel occurrence clears GraphEventId
        await _sut.SignUpAsync(occId, user.Id, "Test topic");
        await _sut.CancelOccurrenceAsync(occId, owner.Id);

        // Uncancel → should recreate meeting for existing signup
        await _sut.UncancelOccurrenceAsync(occId, owner.Id);

        _db.ChangeTracker.Clear();
        var occ = await _db.EventOccurrences.FindAsync(occId);
        Assert.NotNull(occ!.GraphEventId);  // New meeting created

        _calendarMock.Verify(c => c.CreateMeetingAsync(
            It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>()),
            Times.Exactly(2));  // Once for signup, once for uncancel
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
        await _sut.SignUpAsync(firstOcc.Id, user.Id, "Test topic");

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

    [Fact]
    public async Task UpdateEvent_EndTimeBeforeStartTime_ReturnsError()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var updated = new Event
        {
            Id = evt.Id,
            Title = "Bad Times",
            StartTime = new LocalDateTime(2026, 3, 10, 10, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 9, 0),
            Capacity = 5,
            TimeZoneId = "America/Chicago"
        };

        var (success, error) = await _sut.UpdateEventAsync(updated, owner.Id);

        Assert.False(success);
        Assert.Contains("end time", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateEvent_EndTimeEqualsStartTime_ReturnsError()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        var updated = new Event
        {
            Id = evt.Id,
            Title = "Zero Duration",
            StartTime = new LocalDateTime(2026, 3, 10, 10, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 10, 0),
            Capacity = 5,
            TimeZoneId = "America/Chicago"
        };

        var (success, error) = await _sut.UpdateEventAsync(updated, owner.Id);

        Assert.False(success);
        Assert.Contains("end time", error!, StringComparison.OrdinalIgnoreCase);
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
        await _sut.SignUpAsync(occurrenceId, user.Id, "Test topic");

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

    // ── CalendarUpdateNotifier integration ───────────────────────────

    [Fact]
    public async Task CreateEvent_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        Assert.True(notified);
    }

    [Fact]
    public async Task SignUp_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.SignUpAsync(occId, user.Id, "Test topic");

        Assert.True(notified);
    }

    [Fact]
    public async Task CancelSignUp_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;
        await _sut.SignUpAsync(occId, user.Id, "Test topic");

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.CancelSignUpAsync(occId, user.Id);

        Assert.True(notified);
    }

    [Fact]
    public async Task CancelOccurrence_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.CancelOccurrenceAsync(occId, owner.Id);

        Assert.True(notified);
    }

    [Fact]
    public async Task UncancelOccurrence_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;
        await _sut.CancelOccurrenceAsync(occId, owner.Id);

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.UncancelOccurrenceAsync(occId, owner.Id);

        Assert.True(notified);
    }

    [Fact]
    public async Task UpdateEvent_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        var updated = new Event
        {
            Id = evt.Id,
            Title = "Updated",
            StartTime = new LocalDateTime(2026, 3, 10, 10, 0),
            EndTime = new LocalDateTime(2026, 3, 10, 11, 0),
            Capacity = 5,
            TimeZoneId = "America/Chicago"
        };
        await _sut.UpdateEventAsync(updated, owner.Id);

        Assert.True(notified);
    }

    [Fact]
    public async Task TransferOwnership_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var newOwner = await SeedUserAsync("newowner");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.TransferOwnershipAsync(evt.Id, owner.Id, newOwner.Id);

        Assert.True(notified);
    }

    [Fact]
    public async Task DeleteEvent_Success_NotifiesCalendarSubscribers()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);

        bool notified = false;
        using var sub = _notifier.Subscribe(() => notified = true);

        await _sut.DeleteEventAsync(evt.Id, owner.Id);

        Assert.True(notified);
    }

    // ── Signup messages passed to calendar service ───────────────────

    [Fact]
    public async Task SignUp_WithMessage_PassesSignupsWithMessageToCreateMeeting()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, user.Id, "Budget review");

        _calendarMock.Verify(c => c.CreateMeetingAsync(
            It.IsAny<EventOccurrence>(),
            It.IsAny<AppUser>(),
            It.IsAny<AppUser>(),
            It.Is<IReadOnlyList<EventSignup>>(signups =>
                signups.Count == 1 &&
                signups[0].Message == "Budget review" &&
                signups[0].User != null)),
            Times.Once);
    }

    [Fact]
    public async Task SignUp_SecondUser_PassesAllSignupsToAddAttendee()
    {
        var owner = await SeedOwnerAsync();
        var user1 = await SeedUserAsync("user1");
        var user2 = await SeedUserAsync("user2");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, user1.Id, "Topic A");
        await _sut.SignUpAsync(occurrenceId, user2.Id, "Topic B");

        _calendarMock.Verify(c => c.AddAttendeeAsync(
            It.IsAny<string>(),
            It.IsAny<AppUser>(),
            It.Is<AppUser>(u => u.Id == user2.Id),
            It.Is<IReadOnlyList<EventSignup>>(signups =>
                signups.Count == 2 &&
                signups.Any(s => s.Message == "Topic A") &&
                signups.Any(s => s.Message == "Topic B"))),
            Times.Once);
    }

    [Fact]
    public async Task CancelSignUp_PassesRemainingSignupsToRemoveAttendee()
    {
        var owner = await SeedOwnerAsync();
        var user1 = await SeedUserAsync("user1");
        var user2 = await SeedUserAsync("user2");
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, user1.Id, "Topic A");
        await _sut.SignUpAsync(occurrenceId, user2.Id, "Topic B");

        await _sut.CancelSignUpAsync(occurrenceId, user1.Id);

        // Should pass only user2's signup (the remaining one)
        _calendarMock.Verify(c => c.RemoveAttendeeAsync(
            It.IsAny<string>(),
            It.Is<AppUser>(u => u.Id == user1.Id),
            It.Is<IReadOnlyList<EventSignup>>(signups =>
                signups.Count == 1 &&
                signups[0].Message == "Topic B")),
            Times.Once);
    }

    [Fact]
    public async Task SignUp_RequiresMessage()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(MakeSingleEvent(owner.Id), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id, "");

        Assert.False(success);
        Assert.Contains("message", error!, StringComparison.OrdinalIgnoreCase);
    }

    // ── TechMeeting: EventType & Model Tests ────────────────────────

    [Fact]
    public async Task CreateEvent_DefaultType_IsOfficeHours()
    {
        var owner = await SeedOwnerAsync();
        var evt = MakeSingleEvent(owner.Id);

        var result = await _sut.CreateEventAsync(evt, owner.Id);

        var saved = await _db.Events.FindAsync(result.Id);
        Assert.Equal(EventType.OfficeHours, saved!.EventType);
    }

    [Fact]
    public async Task CreateEvent_WithTechMeetingType_SetsEventType()
    {
        var owner = await SeedOwnerAsync();
        var evt = MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting);

        var result = await _sut.CreateEventAsync(evt, owner.Id);

        var saved = await _db.Events.FindAsync(result.Id);
        Assert.Equal(EventType.TechMeeting, saved!.EventType);
    }

    [Fact]
    public async Task OccurrenceContributor_PersistsToDatabase()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrence = await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id);

        _db.Set<OccurrenceContributor>().Add(new OccurrenceContributor
        {
            EventOccurrenceId = occurrence.Id,
            UserId = contributor.Id
        });
        await _db.SaveChangesAsync();

        var saved = await _db.Set<OccurrenceContributor>()
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.EventOccurrenceId == occurrence.Id);
        Assert.NotNull(saved);
        Assert.Equal(contributor.Id, saved!.UserId);
        Assert.Equal("User contributor1", saved.User.DisplayName);
    }

    [Fact]
    public async Task EventOccurrence_DisplayName_PrefixAndSuffix()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, title: "Tech Meeting", eventType: EventType.TechMeeting), owner.Id);
        var occurrence = await _db.EventOccurrences
            .Include(o => o.Event)
            .FirstAsync(o => o.EventId == evt.Id);

        // Default: uses Event.Title when no prefix set
        Assert.Equal("Tech Meeting", occurrence.DisplayName);

        // With prefix only
        occurrence.NamePrefix = "Sprint Review";
        Assert.Equal("Sprint Review", occurrence.DisplayName);

        // With prefix and suffix
        occurrence.NameSuffix = "API Refactoring";
        Assert.Equal("Sprint Review: API Refactoring", occurrence.DisplayName);

        // With suffix but no prefix (falls back to Event.Title)
        occurrence.NamePrefix = null;
        Assert.Equal("Tech Meeting: API Refactoring", occurrence.DisplayName);
    }

    // ── TechMeeting: SetContributorsAsync ───────────────────────────

    [Fact]
    public async Task SetContributors_AsOwner_AssignsContributors()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        Assert.True(success);
        Assert.Null(error);
        var contributors = await _db.OccurrenceContributors
            .Where(c => c.EventOccurrenceId == occurrenceId)
            .ToListAsync();
        Assert.Single(contributors);
        Assert.Equal(contributor.Id, contributors[0].UserId);
    }

    [Fact]
    public async Task SetContributors_AsNonOwner_Fails()
    {
        var owner = await SeedOwnerAsync();
        var nonOwner = await SeedUserAsync("nonowner");
        var contributor = await SeedUserAsync("contributor1");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.SetContributorsAsync(occurrenceId, nonOwner.Id, new List<int> { contributor.Id });

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetContributors_OnOfficeHoursEvent_Fails()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.OfficeHours), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        Assert.False(success);
        Assert.Contains("tech meeting", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetContributors_OnLightningTalks_Fails()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrence = await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id);
        occurrence.IsLightningTalks = true;
        await _db.SaveChangesAsync();

        var (success, error) = await _sut.SetContributorsAsync(occurrence.Id, owner.Id, new List<int> { contributor.Id });

        Assert.False(success);
        Assert.Contains("lightning", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetContributors_CreatesTeamsMeeting()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(
                It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>()))
            .ReturnsAsync("graph-contrib-id");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        _calendarMock.Verify(c => c.CreateMeetingForContributorsAsync(
            It.Is<EventOccurrence>(o => o.Id == occurrenceId),
            It.Is<AppUser>(u => u.Id == owner.Id),
            It.Is<IReadOnlyList<AppUser>>(users => users.Count == 1 && users[0].Id == contributor.Id)),
            Times.Once);

        _db.ChangeTracker.Clear();
        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.Equal("graph-contrib-id", occurrence!.GraphEventId);
    }

    [Fact]
    public async Task SetContributors_RemoveAll_CancelsMeeting()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(
                It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>()))
            .ReturnsAsync("graph-contrib-id");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // First assign a contributor
        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        // Now remove all contributors
        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int>());

        _calendarMock.Verify(c => c.CancelMeetingAsync(
            "graph-contrib-id",
            It.Is<AppUser>(u => u.Id == owner.Id)),
            Times.Once);

        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.Null(occurrence!.GraphEventId);
        Assert.Empty(await _db.OccurrenceContributors.Where(c => c.EventOccurrenceId == occurrenceId).ToListAsync());
    }

    // ── TechMeeting: ToggleLightningTalksAsync ──────────────────────

    [Fact]
    public async Task ToggleLightningTalks_AsOwner_RemovesContributors()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(
                It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>()))
            .ReturnsAsync("graph-contrib-id");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Assign contributor first
        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        // Toggle to lightning talks
        var (success, error) = await _sut.ToggleLightningTalksAsync(occurrenceId, owner.Id, true);

        Assert.True(success);
        Assert.Null(error);

        _db.ChangeTracker.Clear();
        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.True(occurrence!.IsLightningTalks);

        // Contributors should be removed
        Assert.Empty(await _db.OccurrenceContributors.Where(c => c.EventOccurrenceId == occurrenceId).ToListAsync());

        // Meeting should be cancelled
        _calendarMock.Verify(c => c.CancelMeetingAsync("graph-contrib-id", It.Is<AppUser>(u => u.Id == owner.Id)), Times.Once);
        Assert.Null(occurrence.GraphEventId);
    }

    [Fact]
    public async Task ToggleLightningTalks_Off_RemovesSignups()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrence = await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id);

        // Set to lightning talks first
        occurrence.IsLightningTalks = true;
        await _db.SaveChangesAsync();

        // Add a signup (simulating lightning talks signup)
        _db.EventSignups.Add(new EventSignup
        {
            EventOccurrenceId = occurrence.Id,
            UserId = user.Id,
            SignedUpAt = _clock.GetCurrentInstant(),
            Message = "My topic"
        });
        await _db.SaveChangesAsync();

        // Toggle off lightning talks
        var (success, error) = await _sut.ToggleLightningTalksAsync(occurrence.Id, owner.Id, false);

        Assert.True(success);
        Assert.Null(error);

        _db.ChangeTracker.Clear();
        var updated = await _db.EventOccurrences.FindAsync(occurrence.Id);
        Assert.False(updated!.IsLightningTalks);

        // Signups should be removed
        Assert.Empty(await _db.EventSignups.Where(s => s.EventOccurrenceId == occurrence.Id).ToListAsync());
    }

    [Fact]
    public async Task ToggleLightningTalks_OnNonTechMeeting_Fails()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.OfficeHours), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.ToggleLightningTalksAsync(occurrenceId, owner.Id, true);

        Assert.False(success);
        Assert.Contains("tech meeting", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ToggleLightningTalks_On_CancelsMeeting()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(
                It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>()))
            .ReturnsAsync("graph-meeting-id");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        await _sut.ToggleLightningTalksAsync(occurrenceId, owner.Id, true);

        _calendarMock.Verify(c => c.CancelMeetingAsync("graph-meeting-id", It.Is<AppUser>(u => u.Id == owner.Id)), Times.Once);
    }

    [Fact]
    public async Task EventService_Operations_SucceedAfterExternalDbContextDisposed()
    {
        // Mirrors the production ObjectDisposedException: in Blazor Server the scoped
        // AppDbContext can be disposed mid-operation when the circuit tears down. If
        // EventService holds the scoped context directly, the in-flight SaveChangesAsync
        // throws and can orphan a Teams meeting. EventService must use
        // IDbContextFactory<AppDbContext> so each operation owns its own short-lived
        // context that is independent of any external scope.
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;
        await _sut.ToggleLightningTalksAsync(occurrenceId, owner.Id, true, 5);

        // Simulate the circuit/scope disposing the DbContext that EventService was
        // injected with. The underlying SqliteConnection stays open.
        _db.Dispose();

        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id, "My talk");
        Assert.True(success, error);
    }

    [Fact]
    public async Task ToggleLightningTalks_CapacityOnlyChange_PreservesMeetingAndSignups()
    {
        // Reproduces the bug where changing the capacity input on an already-lightning-talks
        // occurrence cancels the Teams meeting and clears GraphEventId, causing subsequent
        // signups to create a duplicate meeting.
        var owner = await SeedOwnerAsync();
        var signupUser = await SeedUserAsync("firstSignup");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting, capacity: 3), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Turn on lightning talks with capacity 3
        await _sut.ToggleLightningTalksAsync(occurrenceId, owner.Id, true, 3);

        // First user signs up — this creates the Teams meeting and persists GraphEventId
        var signup = await _sut.SignUpAsync(occurrenceId, signupUser.Id, "My talk");
        Assert.True(signup.Success, signup.Error);

        _db.ChangeTracker.Clear();
        var originalGraphEventId = (await _db.EventOccurrences.FindAsync(occurrenceId))!.GraphEventId;
        Assert.False(string.IsNullOrEmpty(originalGraphEventId));

        // Owner bumps the capacity — isLightningTalks stays true, only capacity changes
        var (success, error) = await _sut.ToggleLightningTalksAsync(occurrenceId, owner.Id, true, 5);
        Assert.True(success, error);

        // Meeting must NOT be cancelled
        _calendarMock.Verify(c => c.CancelMeetingAsync(
            It.IsAny<string>(), It.IsAny<AppUser>()), Times.Never);

        _db.ChangeTracker.Clear();
        var occurrence = await _db.EventOccurrences
            .Include(o => o.Signups)
            .FirstAsync(o => o.Id == occurrenceId);

        Assert.Equal(originalGraphEventId, occurrence.GraphEventId);
        Assert.True(occurrence.IsLightningTalks);
        Assert.Equal(5, occurrence.LightningTalksCapacity);
        Assert.Single(occurrence.Signups);
    }

    // ── TechMeeting: SignUp Guard ───────────────────────────────────

    [Fact]
    public async Task SignUp_OnRegularTechMeeting_Fails()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.SignUpAsync(occurrenceId, user.Id, "My topic");

        Assert.False(success);
        Assert.Contains("contributor", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignUp_OnLightningTalks_Succeeds()
    {
        var owner = await SeedOwnerAsync();
        var user = await SeedUserAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrence = await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id);
        occurrence.IsLightningTalks = true;
        await _db.SaveChangesAsync();

        var (success, error) = await _sut.SignUpAsync(occurrence.Id, user.Id, "Lightning topic");

        Assert.True(success);
        Assert.Null(error);
    }

    // ── TechMeeting: UpdateOccurrenceNameAsync ──────────────────────

    [Fact]
    public async Task UpdateOccurrenceName_OwnerCanEditPrefix()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.UpdateOccurrenceNameAsync(occurrenceId, owner.Id, "Sprint Review", null);

        Assert.True(success);
        Assert.Null(error);
        _db.ChangeTracker.Clear();
        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.Equal("Sprint Review", occurrence!.NamePrefix);
    }

    [Fact]
    public async Task UpdateOccurrenceName_ContributorCanEditSuffix()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(
                It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>()))
            .ReturnsAsync("graph-id");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Assign contributor
        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        // Contributor edits suffix
        var (success, error) = await _sut.UpdateOccurrenceNameAsync(occurrenceId, contributor.Id, null, "API Design");

        Assert.True(success);
        Assert.Null(error);
        _db.ChangeTracker.Clear();
        var occurrence = await _db.EventOccurrences.FindAsync(occurrenceId);
        Assert.Equal("API Design", occurrence!.NameSuffix);
    }

    [Fact]
    public async Task UpdateOccurrenceName_NonContributorCannotEditSuffix()
    {
        var owner = await SeedOwnerAsync();
        var nonContributor = await SeedUserAsync("outsider");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        var (success, error) = await _sut.UpdateOccurrenceNameAsync(occurrenceId, nonContributor.Id, null, "Sneaky Edit");

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateOccurrenceName_WithExistingMeeting_UpdatesTeamsMeetingSubject()
    {
        var owner = await SeedOwnerAsync();
        var contributor = await SeedUserAsync("contributor1");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(
                It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>()))
            .ReturnsAsync("graph-contrib-id");

        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, title: "Tech Meeting", eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        // Assign a contributor so a Teams meeting exists for this occurrence
        await _sut.SetContributorsAsync(occurrenceId, owner.Id, new List<int> { contributor.Id });

        // Change the topic (suffix) of the tech meeting
        var (success, error) = await _sut.UpdateOccurrenceNameAsync(occurrenceId, contributor.Id, null, "GraphQL Deep Dive");

        Assert.True(success);
        Assert.Null(error);

        // The existing Teams meeting subject should be updated to match the new DisplayName
        _calendarMock.Verify(c => c.UpdateMeetingSubjectAsync(
            "graph-contrib-id",
            "Tech Meeting: GraphQL Deep Dive"),
            Times.Once);
    }

    // ── TechMeeting: Reminders ──────────────────────────────────────

    [Fact]
    public async Task SetReminderDefinitions_AsOwner_CreatesReminders()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);

        var (success, error) = await _sut.SetReminderDefinitionsAsync(
            evt.Id, owner.Id, new List<string> { "Recording Extension", "In Sharepoint" });

        Assert.True(success);
        Assert.Null(error);
        var defs = await _db.Set<EventReminderDefinition>()
            .Where(d => d.EventId == evt.Id)
            .OrderBy(d => d.DisplayOrder)
            .ToListAsync();
        Assert.Equal(2, defs.Count);
        Assert.Equal("Recording Extension", defs[0].Name);
        Assert.Equal("In Sharepoint", defs[1].Name);
    }

    [Fact]
    public async Task SetReminderDefinitions_AsNonOwner_Fails()
    {
        var owner = await SeedOwnerAsync();
        var nonOwner = await SeedUserAsync("nonowner");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);

        var (success, error) = await _sut.SetReminderDefinitionsAsync(
            evt.Id, nonOwner.Id, new List<string> { "Test" });

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetReminderDefinitions_OnOfficeHours_Fails()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.OfficeHours), owner.Id);

        var (success, error) = await _sut.SetReminderDefinitionsAsync(
            evt.Id, owner.Id, new List<string> { "Test" });

        Assert.False(success);
        Assert.Contains("tech meeting", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetReminderDefinitions_Over10_Fails()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);

        var names = Enumerable.Range(1, 11).Select(i => $"Reminder {i}").ToList();
        var (success, error) = await _sut.SetReminderDefinitionsAsync(evt.Id, owner.Id, names);

        Assert.False(success);
        Assert.Contains("10", error!);
    }

    [Fact]
    public async Task SetReminderDefinitions_RemovesDeleted_PreservesExisting()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);

        // Create initial reminders
        await _sut.SetReminderDefinitionsAsync(evt.Id, owner.Id, new List<string> { "A", "B", "C" });

        // Update: keep B, add D, remove A and C
        var (success, _) = await _sut.SetReminderDefinitionsAsync(evt.Id, owner.Id, new List<string> { "B", "D" });

        Assert.True(success);
        var defs = await _db.Set<EventReminderDefinition>()
            .Where(d => d.EventId == evt.Id)
            .OrderBy(d => d.DisplayOrder)
            .ToListAsync();
        Assert.Equal(2, defs.Count);
        Assert.Equal("B", defs[0].Name);
        Assert.Equal("D", defs[1].Name);
    }

    [Fact]
    public async Task SetReminderValue_AsOwner_SetsValue()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SetReminderDefinitionsAsync(evt.Id, owner.Id, new List<string> { "Recording" });
        var def = await _db.Set<EventReminderDefinition>().FirstAsync(d => d.EventId == evt.Id);

        var (success, error) = await _sut.SetReminderValueAsync(occurrenceId, owner.Id, def.Id, true);

        Assert.True(success);
        Assert.Null(error);
        var value = await _db.Set<OccurrenceReminderValue>()
            .FirstOrDefaultAsync(v => v.EventOccurrenceId == occurrenceId && v.ReminderDefinitionId == def.Id);
        Assert.NotNull(value);
        Assert.True(value!.Value);
    }

    [Fact]
    public async Task SetReminderValue_AsNonOwner_Fails()
    {
        var owner = await SeedOwnerAsync();
        var nonOwner = await SeedUserAsync("nonowner");
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SetReminderDefinitionsAsync(evt.Id, owner.Id, new List<string> { "Recording" });
        var def = await _db.Set<EventReminderDefinition>().FirstAsync(d => d.EventId == evt.Id);

        var (success, error) = await _sut.SetReminderValueAsync(occurrenceId, nonOwner.Id, def.Id, true);

        Assert.False(success);
        Assert.Contains("owner", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetReminderValue_ToggleOff_UpdatesExisting()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(
            MakeSingleEvent(owner.Id, eventType: EventType.TechMeeting), owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SetReminderDefinitionsAsync(evt.Id, owner.Id, new List<string> { "Recording" });
        var def = await _db.Set<EventReminderDefinition>().FirstAsync(d => d.EventId == evt.Id);

        // Set to true then false
        await _sut.SetReminderValueAsync(occurrenceId, owner.Id, def.Id, true);
        await _sut.SetReminderValueAsync(occurrenceId, owner.Id, def.Id, false);

        var value = await _db.Set<OccurrenceReminderValue>()
            .FirstAsync(v => v.EventOccurrenceId == occurrenceId && v.ReminderDefinitionId == def.Id);
        Assert.False(value.Value);
    }
}
