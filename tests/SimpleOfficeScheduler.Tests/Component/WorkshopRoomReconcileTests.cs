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
/// A workshop's Graph series can be out of step with the room the app has recorded: it may predate
/// room booking entirely, or have been created during a spell when the room never reached Graph.
/// Nothing used to correct that — SyncWorkshopSeriesAsync only pushed the schedule, so the room was
/// applied on an explicit SetRoomAsync and never again. An edit is the natural place to notice, so
/// every sync now reconciles the room onto the series.
///
/// The booking status has to come back to Pending with it. Patching the schedule or the recurrence
/// range makes the room mailbox re-evaluate every date, and RefreshRoomBookingStatusAsync only
/// polls occurrences that are Pending — so an occurrence left at Booked keeps reporting a booking
/// for a time that has since moved, and one left at Failed is never given another chance.
/// </summary>
public class WorkshopRoomReconcileTests : IDisposable
{
    private const string RoomEmail = "room-a@test.local";

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly EventService _sut;

    public WorkshopRoomReconcileTests()
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

        var rooms = new GraphApiSettings
        {
            RoomBookingWindowDays = 170,
            Rooms = new List<ConfiguredRoom>
            {
                new() { Email = RoomEmail, DisplayName = "Test Room A", Capacity = 8 }
            }
        };

        _sut = new EventService(
            _dbFactory,
            new RecurrenceExpander(),
            _calendarMock.Object,
            new ConfigRoomService(Options.Create(rooms), NullLogger<ConfigRoomService>.Instance),
            Options.Create(new RecurrenceSettings { DefaultHorizonMonths = 6, ExpansionCheckIntervalHours = 24 }),
            Options.Create(rooms),
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

    private async Task<AppUser> AddOwnerAsync()
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

    private static RecurrencePattern Weekly(DayOfWeek day) => new()
    {
        Type = RecurrenceType.Weekly,
        Interval = 1,
        DaysOfWeek = new List<DayOfWeek> { day }
    };

    /// <summary>
    /// A recurring workshop with a room, already synced to Graph once.
    /// </summary>
    private async Task<Event> CreateRecurringWorkshopAsync(int ownerId, string? roomEmail = RoomEmail)
    {
        var start = Today.PlusDays(1).At(new LocalTime(9, 0));
        return await _sut.CreateEventAsync(new Event
        {
            Title = "Recurring Workshop",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop,
            RoomEmail = roomEmail,
            Recurrence = Weekly(start.DayOfWeek.ToDayOfWeek())
        }, ownerId);
    }

    private async Task<AppUser> AddAttendeeAsync()
    {
        var user = new AppUser
        {
            Username = "attendee",
            DisplayName = "Attendee",
            Email = "attendee@test.local",
            IsLocalAccount = true,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Signs someone up for the first occurrence so it survives the edit. UpdateEventAsync drops and
    /// regenerates future occurrences that have no signups, and regenerated ones start at Pending
    /// anyway — the ones that are kept are where a stale status actually persists.
    /// </summary>
    private async Task<int> SignUpToFirstOccurrenceAsync(int eventId)
    {
        var attendee = await AddAttendeeAsync();
        await using var db = _dbFactory.CreateDbContext();
        var occ = await db.EventOccurrences
            .Where(o => o.EventId == eventId)
            .OrderBy(o => o.StartTime)
            .FirstAsync();
        var (ok, error) = await _sut.SignUpAsync(occ.Id, attendee.Id, "");
        Assert.True(ok, error);
        return occ.Id;
    }

    private Event EditOf(Event evt, string title) => new()
    {
        Id = evt.Id,
        Title = title,
        StartTime = evt.StartTime,
        EndTime = evt.EndTime,
        Capacity = evt.Capacity,
        TimeZoneId = evt.TimeZoneId,
        EventType = evt.EventType,
        Recurrence = Weekly(evt.StartTime.DayOfWeek.ToDayOfWeek())
    };

    [Fact]
    public async Task EditingARecurringWorkshop_ReappliesTheRoomToTheSeries()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id);

        Room? captured = null;
        _calendarMock
            .Setup(c => c.UpdateSeriesRoomAsync(It.IsAny<string>(), It.IsAny<Room?>()))
            .Callback((string _, Room? room) => captured = room)
            .Returns(Task.CompletedTask);

        var (success, error) = await _sut.UpdateEventAsync(EditOf(evt, "Renamed"), owner.Id);

        Assert.True(success, error);
        _calendarMock.Verify(c => c.UpdateSeriesRoomAsync("series-id", It.IsAny<Room?>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal(RoomEmail, captured.Email);
    }

    /// <summary>
    /// The case the report describes: a series made before the app booked rooms. It has a
    /// GraphSeriesId and no resource attendee, and only an edit will ever put one there.
    /// </summary>
    [Fact]
    public async Task EditingAWorkshopWhoseSeriesPredatesRoomBooking_PutsTheRoomOnIt()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id, roomEmail: null);

        // The room is chosen later, the way it would be on an event that pre-dates the feature.
        var (roomOk, roomError) = await _sut.SetRoomAsync(evt.Id, owner.Id, RoomEmail);
        Assert.True(roomOk, roomError);
        _calendarMock.Invocations.Clear();

        var (success, error) = await _sut.UpdateEventAsync(EditOf(evt, "Renamed"), owner.Id);

        Assert.True(success, error);
        _calendarMock.Verify(c => c.UpdateSeriesRoomAsync("series-id", It.Is<Room?>(r => r!.Email == RoomEmail)),
            Times.Once);
    }

    [Fact]
    public async Task EditingAWorkshopWithNoRoom_DoesNotPatchARoomOntoTheSeries()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id, roomEmail: null);

        var (success, error) = await _sut.UpdateEventAsync(EditOf(evt, "Renamed"), owner.Id);

        Assert.True(success, error);
        _calendarMock.Verify(c => c.UpdateSeriesRoomAsync(It.IsAny<string>(), It.IsAny<Room?>()), Times.Never);
    }

    [Fact]
    public async Task EditingARecurringWorkshop_PutsEveryFutureOccurrenceBackToPending()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id);
        var keptId = await SignUpToFirstOccurrenceAsync(evt.Id);

        // Pretend the poller settled every date against the old schedule.
        await using (var seed = _dbFactory.CreateDbContext())
        {
            foreach (var occ in await seed.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync())
            {
                occ.RoomBookingStatus = RoomBookingStatus.Booked;
            }
            await seed.SaveChangesAsync();
        }

        await _sut.UpdateEventAsync(EditOf(evt, "Renamed"), owner.Id);

        await using var db = _dbFactory.CreateDbContext();
        var kept = await db.EventOccurrences.FirstAsync(o => o.Id == keptId);
        Assert.Equal(RoomBookingStatus.Pending, kept.RoomBookingStatus);

        var after = await db.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync();
        Assert.NotEmpty(after);
        Assert.All(after, o => Assert.Equal(RoomBookingStatus.Pending, o.RoomBookingStatus));
    }

    /// <summary>
    /// A date the room previously declined has to be asked again after the schedule moves, or the
    /// workshop keeps showing a failure that belongs to a time it no longer runs at.
    /// </summary>
    [Fact]
    public async Task EditingARecurringWorkshop_ClearsAStaleDeclineSoItIsPolledAgain()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id);
        await SignUpToFirstOccurrenceAsync(evt.Id);

        await using (var seed = _dbFactory.CreateDbContext())
        {
            foreach (var occ in await seed.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync())
            {
                occ.RoomBookingStatus = RoomBookingStatus.Declined;
                occ.RoomBookingError = "The room declined the booking.";
            }
            await seed.SaveChangesAsync();
        }

        await _sut.UpdateEventAsync(EditOf(evt, "Renamed"), owner.Id);

        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync();
        Assert.All(after, o =>
        {
            Assert.Equal(RoomBookingStatus.Pending, o.RoomBookingStatus);
            Assert.Null(o.RoomBookingError);
        });
    }

    /// <summary>
    /// Rolling the range forward adds dates the room has never seen. ExtendSeriesRangeAsync patches
    /// the recurrence precisely so the room re-evaluates them, but that answer is only ever read for
    /// occurrences sitting at Pending.
    /// </summary>
    [Fact]
    public async Task ExtendingTheSeriesRange_PutsFutureOccurrencesBackToPending()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id);

        await using (var seed = _dbFactory.CreateDbContext())
        {
            var stored = await seed.Events.FirstAsync(e => e.Id == evt.Id);
            // Due for renewal: the current range ends inside the 30-day lead time.
            stored.GraphSeriesWindowEnd = Today.PlusDays(10);
            foreach (var occ in await seed.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync())
            {
                occ.RoomBookingStatus = RoomBookingStatus.Booked;
            }
            await seed.SaveChangesAsync();
        }

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(1, extended);
        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync();
        Assert.NotEmpty(after);
        Assert.All(after, o => Assert.Equal(RoomBookingStatus.Pending, o.RoomBookingStatus));
    }

    [Fact]
    public async Task ExtendingTheSeriesRangeWithNoRoom_LeavesTheStatusAlone()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id, roomEmail: null);

        await using (var seed = _dbFactory.CreateDbContext())
        {
            var stored = await seed.Events.FirstAsync(e => e.Id == evt.Id);
            stored.GraphSeriesWindowEnd = Today.PlusDays(10);
            await seed.SaveChangesAsync();
        }

        var extended = await _sut.ExtendExpiringWorkshopSeriesAsync();

        Assert.Equal(1, extended);
        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.Where(o => o.EventId == evt.Id).ToListAsync();
        Assert.All(after, o => Assert.Equal(RoomBookingStatus.None, o.RoomBookingStatus));
    }

    // ── Signups that turn an instance into a series exception ───────
    //
    // A signup on a recurring workshop patches one series instance, which Graph records as an
    // exception, and the resolved instance id is cached on the occurrence. That moves what the
    // poller reads from the series master to the instance, so whatever it had already concluded
    // from the master is stale: it describes a Graph object the occurrence no longer points at.

    [Fact]
    public async Task ASignupThatCreatesTheSeriesException_RequeuesThatDateForTheRoom()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id);

        _calendarMock
            .Setup(c => c.GetInstanceIdAsync(It.IsAny<string>(), It.IsAny<LocalDateTime>(), It.IsAny<string>()))
            .ReturnsAsync("instance-1");

        int occurrenceId;
        await using (var seed = _dbFactory.CreateDbContext())
        {
            var occ = await seed.EventOccurrences
                .Where(o => o.EventId == evt.Id).OrderBy(o => o.StartTime).FirstAsync();
            occurrenceId = occ.Id;
            // The poller already settled this date against the series master.
            occ.RoomBookingStatus = RoomBookingStatus.Booked;
            await seed.SaveChangesAsync();
        }

        var attendee = await AddAttendeeAsync();
        var (ok, error) = await _sut.SignUpAsync(occurrenceId, attendee.Id, "");

        Assert.True(ok, error);
        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
        Assert.Equal("instance-1", after.GraphEventId);
        Assert.Equal(RoomBookingStatus.Pending, after.RoomBookingStatus);
    }

    /// <summary>
    /// Only the first signup moves the polling target. Later ones patch an exception that already
    /// exists, so re-queueing on every signup would just flap the badge back to Pending for a whole
    /// poll interval each time somebody joins.
    /// </summary>
    [Fact]
    public async Task ALaterSignupOnTheSameOccurrence_LeavesASettledBookingAlone()
    {
        var owner = await AddOwnerAsync();
        var evt = await CreateRecurringWorkshopAsync(owner.Id);

        _calendarMock
            .Setup(c => c.GetInstanceIdAsync(It.IsAny<string>(), It.IsAny<LocalDateTime>(), It.IsAny<string>()))
            .ReturnsAsync("instance-1");

        var occurrenceId = (await _db.EventOccurrences
            .Where(o => o.EventId == evt.Id).OrderBy(o => o.StartTime).FirstAsync()).Id;

        var first = await AddAttendeeAsync();
        await _sut.SignUpAsync(occurrenceId, first.Id, "");

        // The poller has now answered for the exception itself.
        await using (var seed = _dbFactory.CreateDbContext())
        {
            var occ = await seed.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
            occ.RoomBookingStatus = RoomBookingStatus.Booked;
            await seed.SaveChangesAsync();
        }

        var second = new AppUser
        {
            Username = "second",
            DisplayName = "Second",
            Email = "second@test.local",
            IsLocalAccount = true,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.Add(second);
        await _db.SaveChangesAsync();
        await _sut.SignUpAsync(occurrenceId, second.Id, "");

        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
        Assert.Equal(RoomBookingStatus.Booked, after.RoomBookingStatus);
    }

    /// <summary>
    /// A one-off workshop has no instances: the object created up front is the meeting, so a signup
    /// patches it directly and the poller keeps reading the same id. Nothing has gone stale.
    /// </summary>
    [Fact]
    public async Task ASignupOnAOneOffWorkshop_LeavesASettledBookingAlone()
    {
        var owner = await AddOwnerAsync();
        var start = Today.PlusDays(1).At(new LocalTime(9, 0));
        var evt = await _sut.CreateEventAsync(new Event
        {
            Title = "One-off Workshop",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 10,
            TimeZoneId = "America/Chicago",
            EventType = EventType.Workshop,
            RoomEmail = RoomEmail
        }, owner.Id);

        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;
        await using (var seed = _dbFactory.CreateDbContext())
        {
            var occ = await seed.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
            occ.RoomBookingStatus = RoomBookingStatus.Booked;
            await seed.SaveChangesAsync();
        }

        var attendee = await AddAttendeeAsync();
        await _sut.SignUpAsync(occurrenceId, attendee.Id, "");

        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
        Assert.Equal(RoomBookingStatus.Booked, after.RoomBookingStatus);
        _calendarMock.Verify(
            c => c.GetInstanceIdAsync(It.IsAny<string>(), It.IsAny<LocalDateTime>(), It.IsAny<string>()),
            Times.Never);
    }
}
