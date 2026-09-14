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
/// Office hours and tech meetings do not get a Graph meeting until something needs one: the first
/// signup, or the first contributor assignment. A room chosen before then has to be attached when
/// that meeting is finally created, because there is nothing to patch it onto beforehand —
/// SetRoomAsync only patches occurrences that already have a GraphEventId.
///
/// Without this the room was recorded on the event, the occurrences sat at Pending forever, and the
/// meeting was eventually created with no resource attendee. The poller then found no room on the
/// meeting and reported Failed for a booking the app had never actually sent.
/// </summary>
public class RoomOnFirstMeetingTests : IDisposable
{
    private const string RoomEmail = "room-a@test.local";

    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ICalendarInviteService> _calendarMock;
    private readonly FakeClock _clock;
    private readonly EventService _sut;

    public RoomOnFirstMeetingTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _dbFactory = new TestDbContextFactory(options);
        _db = _dbFactory.CreateDbContext();
        _db.Database.EnsureCreated();

        _calendarMock = new Mock<ICalendarInviteService>();
        _calendarMock
            .Setup(c => c.CreateMeetingAsync(It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(),
                It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>(), It.IsAny<Room?>()))
            .ReturnsAsync("meeting-id");
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(It.IsAny<EventOccurrence>(),
                It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>(), It.IsAny<Room?>()))
            .ReturnsAsync("meeting-id");

        _clock = new FakeClock(Instant.FromUtc(2026, 3, 1, 12, 0));

        var rooms = new GraphApiSettings
        {
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

    private async Task<AppUser> AddUserAsync(string username)
    {
        var user = new AppUser
        {
            Username = username,
            DisplayName = username,
            Email = $"{username}@test.local",
            IsLocalAccount = true,
            CreatedAt = _clock.GetCurrentInstant()
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    private async Task<Event> CreateEventWithRoomAsync(EventType type, int ownerId, string? roomEmail = RoomEmail)
    {
        var start = _clock.GetCurrentInstant()
            .InZone(TimeZoneHelper.GetZone("America/Chicago")).LocalDateTime
            .Date.PlusDays(1).At(new LocalTime(9, 0));

        return await _sut.CreateEventAsync(new Event
        {
            Title = $"{type} with a room",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 5,
            TimeZoneId = "America/Chicago",
            EventType = type,
            RoomEmail = roomEmail
        }, ownerId);
    }

    [Fact]
    public async Task SigningUpForOfficeHoursWithARoom_PutsTheRoomOnTheNewMeeting()
    {
        var owner = await AddUserAsync("owner");
        var signee = await AddUserAsync("signee");
        var evt = await CreateEventWithRoomAsync(EventType.OfficeHours, owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        Room? captured = null;
        _calendarMock
            .Setup(c => c.CreateMeetingAsync(It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(),
                It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>(), It.IsAny<Room?>()))
            .Callback((EventOccurrence _, AppUser _, AppUser _, IReadOnlyList<EventSignup> _, Room? room) =>
                captured = room)
            .ReturnsAsync("meeting-id");

        var (success, error) = await _sut.SignUpAsync(occurrenceId, signee.Id, "A topic");

        Assert.True(success, error);
        Assert.NotNull(captured);
        Assert.Equal(RoomEmail, captured.Email);
        Assert.Equal("Test Room A", captured.DisplayName);
    }

    [Fact]
    public async Task SigningUpForOfficeHoursWithoutARoom_PassesNoRoom()
    {
        var owner = await AddUserAsync("owner");
        var signee = await AddUserAsync("signee");
        var evt = await CreateEventWithRoomAsync(EventType.OfficeHours, owner.Id, roomEmail: null);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, signee.Id, "A topic");

        _calendarMock.Verify(c => c.CreateMeetingAsync(It.IsAny<EventOccurrence>(), It.IsAny<AppUser>(),
            It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<EventSignup>>(), null), Times.Once);
    }

    [Fact]
    public async Task AssigningContributorsWithARoom_PutsTheRoomOnTheNewMeeting()
    {
        var owner = await AddUserAsync("owner");
        var contributor = await AddUserAsync("contributor");
        var evt = await CreateEventWithRoomAsync(EventType.TechMeeting, owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        Room? captured = null;
        _calendarMock
            .Setup(c => c.CreateMeetingForContributorsAsync(It.IsAny<EventOccurrence>(),
                It.IsAny<AppUser>(), It.IsAny<IReadOnlyList<AppUser>>(), It.IsAny<Room?>()))
            .Callback((EventOccurrence _, AppUser _, IReadOnlyList<AppUser> _, Room? room) =>
                captured = room)
            .ReturnsAsync("meeting-id");

        var (success, error) = await _sut.SetContributorsAsync(
            occurrenceId, owner.Id, new List<int> { contributor.Id });

        Assert.True(success, error);
        Assert.NotNull(captured);
        Assert.Equal(RoomEmail, captured.Email);
    }

    [Fact]
    public async Task CancellingTheLastSignup_ReturnsTheOccurrenceToPending()
    {
        var owner = await AddUserAsync("owner");
        var signee = await AddUserAsync("signee");
        var evt = await CreateEventWithRoomAsync(EventType.OfficeHours, owner.Id);
        var occurrenceId = (await _db.EventOccurrences.FirstAsync(o => o.EventId == evt.Id)).Id;

        await _sut.SignUpAsync(occurrenceId, signee.Id, "A topic");

        // Pretend the poller confirmed the booking on the meeting the signup created.
        await using (var seed = _dbFactory.CreateDbContext())
        {
            var occ = await seed.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
            occ.RoomBookingStatus = RoomBookingStatus.Booked;
            await seed.SaveChangesAsync();
        }

        // The last signup leaving cancels the meeting, which releases the room with it. Leaving the
        // status at Booked would claim a room the app no longer holds.
        var (success, error) = await _sut.CancelSignUpAsync(occurrenceId, signee.Id);

        Assert.True(success, error);
        await using var db = _dbFactory.CreateDbContext();
        var after = await db.EventOccurrences.FirstAsync(o => o.Id == occurrenceId);
        Assert.Null(after.GraphEventId);
        Assert.Equal(RoomBookingStatus.Pending, after.RoomBookingStatus);
    }

    /// <summary>
    /// A workshop does get its series at creation time, and CreateEventAsync has always passed the
    /// room to it. Asserted here because every other mock of CreateSeriesAsync matches the room
    /// with It.IsAny&lt;Room?&gt;(), so a null would have gone unnoticed — which is how the panel
    /// dropping RoomEmail stayed invisible to the whole suite.
    /// </summary>
    [Fact]
    public async Task CreatingAWorkshopWithARoom_PutsTheRoomOnTheSeries()
    {
        var owner = await AddUserAsync("owner");

        Room? captured = null;
        _calendarMock
            .Setup(c => c.CreateSeriesAsync(It.IsAny<Event>(), It.IsAny<IReadOnlyList<AppUser>>(),
                It.IsAny<LocalDate>(), It.IsAny<Room?>()))
            .Callback((Event _, IReadOnlyList<AppUser> _, LocalDate _, Room? room) => captured = room)
            .ReturnsAsync("series-id");

        await CreateEventWithRoomAsync(EventType.Workshop, owner.Id);

        Assert.NotNull(captured);
        Assert.Equal(RoomEmail, captured.Email);
    }

    [Fact]
    public async Task CreatingAnyEventWithAnUnknownRoom_Throws()
    {
        var owner = await AddUserAsync("owner");

        // Resolved before anything is written, so a typo fails loudly rather than booking nothing.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateEventWithRoomAsync(EventType.Workshop, owner.Id, roomEmail: "nope@test.local"));
    }
}
