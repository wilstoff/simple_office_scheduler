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
/// UpdateEventAsync regenerated the app's occurrences but never touched the Graph series, so turning
/// an existing workshop into a recurring one changed nothing on anyone's calendar: the meeting stayed
/// a single non-recurring event while the app showed a full schedule.
/// </summary>
public class WorkshopEditSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly EventService _sut;

    public WorkshopEditSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _dbFactory = new TestDbContextFactory(options);
        _db = _dbFactory.CreateDbContext();
        _db.Database.EnsureCreated();

        _calendarMock = new Mock<ICalendarInviteService>();
        _calendarMock
            .Setup(c => c.CreateSeriesAsync(It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(),
                It.IsAny<LocalDate>(), It.IsAny<Room?>()))
            .ReturnsAsync("series-id");

        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        _sut = new EventService(
            _dbFactory,
            new RecurrenceExpander(),
            _calendarMock.Object,
            new ConfigRoomService(Options.Create(new GraphApiSettings()), NullLogger<ConfigRoomService>.Instance),
            Options.Create(new RecurrenceSettings { DefaultHorizonMonths = 6, ExpansionCheckIntervalHours = 24 }),
            Options.Create(new GraphApiSettings { RoomBookingWindowDays = 170 }),
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

    private LocalDate Today =>
        _clock.GetCurrentInstant().InZone(TimeZoneHelper.GetZone("America/Chicago")).LocalDateTime.Date;

    private async Task<AppUser> SeedOwnerAsync()
    {
        var owner = new AppUser
        {
            Username = "owner", DisplayName = "Owner", Email = "owner@test.local",
            IsLocalAccount = true, CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.Add(owner);
        await _db.SaveChangesAsync();
        return owner;
    }

    private LocalDateTime Start => Today.PlusDays(7).At(new LocalTime(9, 0));

    private async Task<Event> CreateOneOffWorkshopAsync(int ownerId) =>
        await _sut.CreateEventAsync(new Event
        {
            Title = "LLM-AI Workshop",
            StartTime = Start,
            EndTime = Start.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop
        }, ownerId);

    private static RecurrencePattern Weekly(LocalDateTime start) => new()
    {
        Type = RecurrenceType.Weekly,
        Interval = 1,
        DaysOfWeek = new List<DayOfWeek> { start.Date.DayOfWeek.ToDayOfWeek() }
    };

    [Fact]
    public async Task MakingAWorkshopRecurring_PushesTheRecurrenceToTheGraphSeries()
    {
        var owner = await SeedOwnerAsync();
        var evt = await CreateOneOffWorkshopAsync(owner.Id);
        _calendarMock.Invocations.Clear();

        var (success, error) = await _sut.UpdateEventAsync(new Event
        {
            Id = evt.Id,
            Title = evt.Title,
            StartTime = Start,
            EndTime = Start.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            Recurrence = Weekly(Start)
        }, owner.Id);

        Assert.True(success, error);

        _calendarMock.Verify(c => c.UpdateSeriesScheduleAsync(
            "series-id",
            It.Is<Event>(e => e.Recurrence != null && e.Recurrence.Type == RecurrenceType.Weekly),
            Today.PlusDays(170)), Times.Once);
    }

    [Fact]
    public async Task MakingAWorkshopRecurring_AlsoExpandsTheAppsOccurrences()
    {
        var owner = await SeedOwnerAsync();
        var evt = await CreateOneOffWorkshopAsync(owner.Id);

        await _sut.UpdateEventAsync(new Event
        {
            Id = evt.Id,
            Title = evt.Title,
            StartTime = Start,
            EndTime = Start.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            Recurrence = Weekly(Start)
        }, owner.Id);

        await using var db = _dbFactory.CreateDbContext();
        Assert.True(db.EventOccurrences.Count(o => o.EventId == evt.Id) > 1);
    }

    [Fact]
    public async Task EditingTheTime_PushesTheNewScheduleToTheGraphSeries()
    {
        var owner = await SeedOwnerAsync();
        var evt = await CreateOneOffWorkshopAsync(owner.Id);
        _calendarMock.Invocations.Clear();

        var moved = Start.PlusHours(3);
        var (success, error) = await _sut.UpdateEventAsync(new Event
        {
            Id = evt.Id,
            Title = "LLM-AI Workshop v2",
            StartTime = moved,
            EndTime = moved.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago"
        }, owner.Id);

        Assert.True(success, error);
        _calendarMock.Verify(c => c.UpdateSeriesScheduleAsync(
            "series-id",
            It.Is<Event>(e => e.StartTime == moved && e.Title == "LLM-AI Workshop v2"),
            It.IsAny<LocalDate>()), Times.Once);
    }

    [Fact]
    public async Task AWorkshopWhoseSeriesCreationFailed_GetsOneOnTheNextEdit()
    {
        var owner = await SeedOwnerAsync();

        // Simulate the series never having been created, which is what prod is left with when the
        // Graph call fails at creation time.
        _calendarMock
            .Setup(c => c.CreateSeriesAsync(It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(),
                It.IsAny<LocalDate>(), It.IsAny<Room?>()))
            .ThrowsAsync(new InvalidOperationException("Graph down"));

        var evt = await CreateOneOffWorkshopAsync(owner.Id);

        await using (var check = _dbFactory.CreateDbContext())
            Assert.Null((await check.Events.FirstAsync(e => e.Id == evt.Id)).GraphSeriesId);

        _calendarMock
            .Setup(c => c.CreateSeriesAsync(It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(),
                It.IsAny<LocalDate>(), It.IsAny<Room?>()))
            .ReturnsAsync("recovered-series-id");

        await _sut.UpdateEventAsync(new Event
        {
            Id = evt.Id,
            Title = evt.Title,
            StartTime = Start,
            EndTime = Start.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            Recurrence = Weekly(Start)
        }, owner.Id);

        await using var db = _dbFactory.CreateDbContext();
        Assert.Equal("recovered-series-id", (await db.Events.FirstAsync(e => e.Id == evt.Id)).GraphSeriesId);
    }

    [Fact]
    public async Task EditingAWorkshop_ClearsCachedInstanceIdsSoTheyAreResolvedAgain()
    {
        var owner = await SeedOwnerAsync();
        var evt = await CreateOneOffWorkshopAsync(owner.Id);

        // Pretend a signup had already cached an instance id against the old schedule.
        await using (var seed = _dbFactory.CreateDbContext())
        {
            var occ = seed.EventOccurrences.First(o => o.EventId == evt.Id);
            occ.GraphEventId = "stale-instance-id";
            await seed.SaveChangesAsync();
        }

        await _sut.UpdateEventAsync(new Event
        {
            Id = evt.Id,
            Title = evt.Title,
            StartTime = Start,
            EndTime = Start.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            Recurrence = Weekly(Start)
        }, owner.Id);

        await using var db = _dbFactory.CreateDbContext();
        Assert.All(db.EventOccurrences.Where(o => o.EventId == evt.Id).ToList(),
            o => Assert.NotEqual("stale-instance-id", o.GraphEventId));
    }

    [Fact]
    public async Task EditingOfficeHours_DoesNotTouchTheSeriesApi()
    {
        var owner = await SeedOwnerAsync();
        var evt = await _sut.CreateEventAsync(new Event
        {
            Title = "Office Hours",
            StartTime = Start,
            EndTime = Start.PlusHours(1),
            Capacity = 5,
            TimeZoneId = "America/Chicago",
            EventType = EventType.OfficeHours
        }, owner.Id);
        _calendarMock.Invocations.Clear();

        await _sut.UpdateEventAsync(new Event
        {
            Id = evt.Id,
            Title = "Office Hours",
            StartTime = Start,
            EndTime = Start.PlusHours(1),
            Capacity = 5,
            TimeZoneId = "America/Chicago",
            Recurrence = Weekly(Start)
        }, owner.Id);

        _calendarMock.Verify(c => c.UpdateSeriesScheduleAsync(
            It.IsAny<string>(), It.IsAny<Event>(), It.IsAny<LocalDate>()), Times.Never);
        _calendarMock.Verify(c => c.CreateSeriesAsync(
            It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(), It.IsAny<LocalDate>(), It.IsAny<Room?>()),
            Times.Never);
    }
}
