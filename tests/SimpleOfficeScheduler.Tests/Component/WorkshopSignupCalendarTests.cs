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
/// A signup on a workshop occurrence patches the attendee list on the Graph object covering that
/// date. For a recurring workshop that means resolving the series instance; for a one-off workshop
/// the Graph object IS the meeting, and asking Graph for its instances fails with
/// "ExpandSeries can only be performed against a series."
/// </summary>
public class WorkshopSignupCalendarTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly EventService _sut;

    public WorkshopSignupCalendarTests()
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
        _calendarMock
            .Setup(c => c.GetInstanceIdAsync(It.IsAny<string>(), It.IsAny<LocalDateTime>(), It.IsAny<string>()))
            .ReturnsAsync("instance-id");

        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        _sut = new EventService(
            _dbFactory,
            new RecurrenceExpander(),
            _calendarMock.Object,
            new ConfigRoomService(Options.Create(new GraphApiSettings()), NullLogger<ConfigRoomService>.Instance),
            Options.Create(new RecurrenceSettings { DefaultHorizonMonths = 6, ExpansionCheckIntervalHours = 24 }),
            Options.Create(new GraphApiSettings()),
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

    private async Task<(AppUser Owner, AppUser Attendee)> SeedUsersAsync()
    {
        var owner = new AppUser
        {
            Username = "owner", DisplayName = "Owner", Email = "owner@test.local",
            IsLocalAccount = true, CreatedAt = _clock.GetCurrentInstant()
        };
        var attendee = new AppUser
        {
            Username = "attendee", DisplayName = "Attendee", Email = "attendee@test.local",
            IsLocalAccount = true, CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.AddRange(owner, attendee);
        await _db.SaveChangesAsync();
        return (owner, attendee);
    }

    private async Task<Event> CreateWorkshopAsync(int ownerId, RecurrencePattern? recurrence)
    {
        var start = Today.PlusDays(7).At(new LocalTime(9, 0));
        return await _sut.CreateEventAsync(new Event
        {
            Title = "LLM-AI Workshop",
            StartTime = start,
            EndTime = start.PlusHours(2),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop,
            Recurrence = recurrence
        }, ownerId);
    }

    [Fact]
    public async Task NonRecurringWorkshop_SignupPatchesTheEventDirectly_WithoutAskingForInstances()
    {
        var (owner, attendee) = await SeedUsersAsync();
        var evt = await CreateWorkshopAsync(owner.Id, recurrence: null);

        await using var db = _dbFactory.CreateDbContext();
        var occurrenceId = db.EventOccurrences.First(o => o.EventId == evt.Id).Id;

        var (success, error) = await _sut.SignUpAsync(occurrenceId, attendee.Id, "");

        Assert.True(success, error);

        // A one-off workshop has no instances; asking for them is the ODataError from prod.
        _calendarMock.Verify(c => c.GetInstanceIdAsync(
            It.IsAny<string>(), It.IsAny<LocalDateTime>(), It.IsAny<string>()), Times.Never);

        _calendarMock.Verify(c => c.PatchInstanceAttendeesAsync(
            "series-id",
            It.Is<IReadOnlyList<AppUser>>(o => o.Count == 1),
            It.Is<IReadOnlyList<EventSignup>>(s => s.Count == 1)), Times.Once);
    }

    [Fact]
    public async Task RecurringWorkshop_SignupResolvesTheInstanceFirst()
    {
        var (owner, attendee) = await SeedUsersAsync();
        var start = Today.PlusDays(7);
        var evt = await CreateWorkshopAsync(owner.Id, new RecurrencePattern
        {
            Type = RecurrenceType.Weekly,
            Interval = 1,
            DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() }
        });

        await using var db = _dbFactory.CreateDbContext();
        var occurrenceId = db.EventOccurrences.Where(o => o.EventId == evt.Id).OrderBy(o => o.StartTime).First().Id;

        var (success, error) = await _sut.SignUpAsync(occurrenceId, attendee.Id, "");

        Assert.True(success, error);
        _calendarMock.Verify(c => c.GetInstanceIdAsync("series-id", It.IsAny<LocalDateTime>(), "America/Chicago"), Times.Once);
        _calendarMock.Verify(c => c.PatchInstanceAttendeesAsync(
            "instance-id", It.IsAny<IReadOnlyList<AppUser>>(), It.IsAny<IReadOnlyList<EventSignup>>()), Times.Once);
    }

    [Fact]
    public async Task NonRecurringWorkshop_CancellingASignupAlsoPatchesDirectly()
    {
        var (owner, attendee) = await SeedUsersAsync();
        var evt = await CreateWorkshopAsync(owner.Id, recurrence: null);

        await using var db = _dbFactory.CreateDbContext();
        var occurrenceId = db.EventOccurrences.First(o => o.EventId == evt.Id).Id;

        await _sut.SignUpAsync(occurrenceId, attendee.Id, "");
        _calendarMock.Invocations.Clear();

        var (success, error) = await _sut.CancelSignUpAsync(occurrenceId, attendee.Id);

        Assert.True(success, error);
        _calendarMock.Verify(c => c.GetInstanceIdAsync(
            It.IsAny<string>(), It.IsAny<LocalDateTime>(), It.IsAny<string>()), Times.Never);
        _calendarMock.Verify(c => c.PatchInstanceAttendeesAsync(
            "series-id",
            It.IsAny<IReadOnlyList<AppUser>>(),
            It.Is<IReadOnlyList<EventSignup>>(s => s.Count == 0)), Times.Once);
    }

    [Fact]
    public async Task NonRecurringWorkshop_KeepsTheGraphSeriesIdAfterTheLastSignupLeaves()
    {
        var (owner, attendee) = await SeedUsersAsync();
        var evt = await CreateWorkshopAsync(owner.Id, recurrence: null);

        await using var db = _dbFactory.CreateDbContext();
        var occurrenceId = db.EventOccurrences.First(o => o.EventId == evt.Id).Id;

        await _sut.SignUpAsync(occurrenceId, attendee.Id, "");
        await _sut.CancelSignUpAsync(occurrenceId, attendee.Id);

        await using var check = _dbFactory.CreateDbContext();
        var stored = await check.Events.FirstAsync(e => e.Id == evt.Id);
        Assert.Equal("series-id", stored.GraphSeriesId);
        _calendarMock.Verify(c => c.CancelMeetingAsync(It.IsAny<string>(), It.IsAny<AppUser>()), Times.Never);
    }
}
