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

/// <summary>
/// A workshop's Graph series only ever extends as far as a room mailbox will accept a booking
/// (BookingWindowInDays, 180 by default). The range is rolled forward before it lapses rather than
/// creating a fresh series, so attendees keep one invite and the per-instance signup exceptions
/// stay attached to the series that owns them.
/// </summary>
public class WorkshopSeriesRenewalTests : IDisposable
{
    private const int WindowDays = 170;
    private const int RenewWithinDays = 30;

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly EventService _sut;

    public WorkshopSeriesRenewalTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _dbFactory = new TestDbContextFactory(options);
        _db = _dbFactory.CreateDbContext();
        _db.Database.EnsureCreated();

        _calendarMock = new Mock<ICalendarInviteService>();
        _calendarMock
            .Setup(c => c.CreateSeriesAsync(It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(), It.IsAny<LocalDate>(), It.IsAny<Room?>()))
            .ReturnsAsync("series-id");

        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        _sut = new EventService(
            _dbFactory,
            new RecurrenceExpander(),
            _calendarMock.Object,
            new ConfigRoomService(Options.Create(new GraphApiSettings()), NullLogger<ConfigRoomService>.Instance),
            Options.Create(new RecurrenceSettings { DefaultHorizonMonths = 6, ExpansionCheckIntervalHours = 24 }),
            Options.Create(new GraphApiSettings { RoomBookingWindowDays = WindowDays }),
            _clock,
            NullLogger<EventService>.Instance,
            new CalendarUpdateNotifier());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    private LocalDate Today => _clock.GetCurrentInstant().InZone(TimeZoneHelperZone).LocalDateTime.Date;
    private static readonly DateTimeZone TimeZoneHelperZone =
        SimpleOfficeScheduler.Services.TimeZoneHelper.GetZone("America/Chicago");

    private async Task<Event> SeedWorkshopAsync(LocalDate? windowEnd, string? seriesId = "series-id")
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

        var start = Today.PlusDays(1).At(new LocalTime(9, 0));
        var evt = new Event
        {
            Title = "Kubernetes Workshop",
            OwnerUserId = owner.Id,
            StartTime = start,
            EndTime = start.PlusHours(1),
            DurationMinutes = 60,
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop,
            GraphSeriesId = seriesId,
            GraphSeriesWindowEnd = windowEnd,
            Recurrence = new RecurrencePattern
            {
                Type = RecurrenceType.Weekly,
                Interval = 1,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() }
            },
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _db.Events.Add(evt);
        await _db.SaveChangesAsync();
        return evt;
    }

    [Fact]
    public async Task CreateWorkshop_RecordsTheSeriesWindowEnd()
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

        var start = Today.PlusDays(1).At(new LocalTime(9, 0));
        var created = await _sut.CreateEventAsync(new Event
        {
            Title = "Kubernetes Workshop",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop
        }, owner.Id);

        await using var db = _dbFactory.CreateDbContext();
        var stored = await db.Events.FirstAsync(e => e.Id == created.Id);
        Assert.Equal(Today.PlusDays(WindowDays), stored.GraphSeriesWindowEnd);
    }

    [Fact]
    public async Task CreateWorkshop_PutsEachOwnerOnTheSeriesExactlyOnce()
    {
        // EF's relationship fixup already adds a new EventOwner to Event.CoOwners, so adding it by
        // hand as well duplicated every co-owner on the meeting invite.
        var owner = new AppUser
        {
            Username = "owner", DisplayName = "Owner", Email = "owner@test.local",
            IsLocalAccount = true, CreatedAt = _clock.GetCurrentInstant()
        };
        var alice = new AppUser
        {
            Username = "alice", DisplayName = "Alice", Email = "alice@test.local",
            IsLocalAccount = true, CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.AddRange(owner, alice);
        await _db.SaveChangesAsync();

        List<AppUser>? capturedOwners = null;
        _calendarMock
            .Setup(c => c.CreateSeriesAsync(It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(),
                It.IsAny<LocalDate>(), It.IsAny<Room?>()))
            .Callback<Event, IReadOnlyList<AppUser>, LocalDate, Room?>((_, o, _, _) => capturedOwners = o.ToList())
            .ReturnsAsync("series-id");

        var start = Today.PlusDays(1).At(new LocalTime(9, 0));
        await _sut.CreateEventAsync(new Event
        {
            Title = "Kubernetes Workshop",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop
        }, owner.Id, new List<int> { alice.Id });

        Assert.NotNull(capturedOwners);
        Assert.Equal(new[] { "Owner", "Alice" }, capturedOwners!.Select(u => u.DisplayName));
    }

    [Fact]
    public async Task SeriesExpiringSoon_IsExtended()
    {
        var evt = await SeedWorkshopAsync(windowEnd: Today.PlusDays(RenewWithinDays - 1));

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(1, extended);
        _calendarMock.Verify(c => c.ExtendSeriesRangeAsync(
            "series-id", It.IsAny<Event>(), Today.PlusDays(WindowDays)), Times.Once);

        await using var db = _dbFactory.CreateDbContext();
        var stored = await db.Events.FirstAsync(e => e.Id == evt.Id);
        Assert.Equal(Today.PlusDays(WindowDays), stored.GraphSeriesWindowEnd);
    }

    [Fact]
    public async Task SeriesWithPlentyOfRunway_IsLeftAlone()
    {
        await SeedWorkshopAsync(windowEnd: Today.PlusDays(RenewWithinDays + 5));

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(0, extended);
        _calendarMock.Verify(c => c.ExtendSeriesRangeAsync(
            It.IsAny<string>(), It.IsAny<Event>(), It.IsAny<LocalDate>()), Times.Never);
    }

    [Fact]
    public async Task WorkshopWithoutASeries_IsSkipped()
    {
        await SeedWorkshopAsync(windowEnd: Today.PlusDays(1), seriesId: null);

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(0, extended);
        _calendarMock.Verify(c => c.ExtendSeriesRangeAsync(
            It.IsAny<string>(), It.IsAny<Event>(), It.IsAny<LocalDate>()), Times.Never);
    }

    [Fact]
    public async Task SeriesEndingBeforeTheRecurrenceEndDate_StopsAtTheRecurrenceEndDate()
    {
        // A workshop that finishes inside the window has nothing left to extend into.
        var evt = await SeedWorkshopAsync(windowEnd: Today.PlusDays(RenewWithinDays - 1));
        evt.Recurrence!.RecurrenceEndDate = Today.PlusDays(RenewWithinDays - 1);
        await _db.SaveChangesAsync();

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(0, extended);
        _calendarMock.Verify(c => c.ExtendSeriesRangeAsync(
            It.IsAny<string>(), It.IsAny<Event>(), It.IsAny<LocalDate>()), Times.Never);
    }

    [Fact]
    public async Task ExtendFailure_LeavesTheStoredWindowUnchangedSoItRetries()
    {
        var evt = await SeedWorkshopAsync(windowEnd: Today.PlusDays(RenewWithinDays - 1));
        var originalWindow = evt.GraphSeriesWindowEnd;

        _calendarMock
            .Setup(c => c.ExtendSeriesRangeAsync(It.IsAny<string>(), It.IsAny<Event>(), It.IsAny<LocalDate>()))
            .ThrowsAsync(new InvalidOperationException("Graph is down"));

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(0, extended);
        await using var db = _dbFactory.CreateDbContext();
        var stored = await db.Events.FirstAsync(e => e.Id == evt.Id);
        Assert.Equal(originalWindow, stored.GraphSeriesWindowEnd);
    }

    [Fact]
    public async Task NonWorkshopEvents_AreIgnored()
    {
        var evt = await SeedWorkshopAsync(windowEnd: Today.PlusDays(1));
        evt.EventType = EventType.OfficeHours;
        await _db.SaveChangesAsync();

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(0, extended);
    }
}
