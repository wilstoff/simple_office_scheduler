using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph.Models;
using Microsoft.Extensions.Options;
using NodaTime;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Rooms;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Room discovery needs the Place.Read.All application permission, which is not mailbox-scoped and
/// so is unaffected by an Application Access Policy. Availability uses getSchedule, which IS
/// mailbox-scoped: a RestrictAccess policy covering only the target mailbox can return an
/// all-zeros availabilityView rather than an error, which is indistinguishable from "every room is
/// free". These tests pin the degradation behavior for both.
/// </summary>
public class RoomServiceTests
{
    private static GraphApiSettings SettingsWithRooms(params (string Email, string Name, int Capacity)[] rooms) =>
        new()
        {
            Rooms = rooms.Select(r => new ConfiguredRoom
            {
                Email = r.Email,
                DisplayName = r.Name,
                Capacity = r.Capacity
            }).ToList()
        };

    private static ConfigRoomService MakeConfigService(GraphApiSettings settings) =>
        new(Options.Create(settings), NullLogger<ConfigRoomService>.Instance);

    [Fact]
    public async Task ConfigRoomService_ReturnsTheConfiguredRooms()
    {
        var sut = MakeConfigService(SettingsWithRooms(
            ("room-a@test.local", "Room A", 8),
            ("room-b@test.local", "Room B", 20)));

        var rooms = await sut.GetRoomsAsync();

        Assert.Equal(2, rooms.Count);
        Assert.Equal("Room A", rooms[0].DisplayName);
        Assert.Equal(8, rooms[0].Capacity);
        Assert.Equal("room-b@test.local", rooms[1].Email);
    }

    [Fact]
    public async Task ConfigRoomService_WithNoConfiguredRooms_ReturnsEmpty()
    {
        var sut = MakeConfigService(new GraphApiSettings());

        Assert.Empty(await sut.GetRoomsAsync());
    }

    [Fact]
    public async Task ConfigRoomService_HasNoAvailabilityData()
    {
        // Free/busy needs Graph. Returning null rather than "all free" keeps the picker honest.
        var sut = MakeConfigService(SettingsWithRooms(("room-a@test.local", "Room A", 8)));

        var availability = await sut.GetAvailabilityAsync(
            new[] { "room-a@test.local" },
            new LocalDateTime(2026, 9, 1, 9, 0),
            new LocalDateTime(2026, 9, 1, 10, 0),
            "America/Chicago");

        Assert.Null(availability);
    }

    [Fact]
    public void Availability_WhenGraphCouldNotReadAnyRoom_CountsAsNoData()
    {
        // A RestrictAccess policy that excludes room mailboxes sets Error per schedule and reports
        // the room as free. Treating that as "free" would advertise busy rooms as available.
        var schedules = new[]
        {
            new ScheduleInformation { ScheduleId = "room-a@test.local", AvailabilityView = "0000", Error = new FreeBusyError() },
            new ScheduleInformation { ScheduleId = "room-b@test.local", AvailabilityView = "0000", Error = new FreeBusyError() }
        };

        Assert.False(GraphRoomService.HasUsableAvailability(schedules));
    }

    [Fact]
    public void Availability_WhenEveryRoomIsGenuinelyFree_IsStillUsable()
    {
        // All-free is the common case for a well-chosen slot, and is exactly when the grid helps.
        var schedules = new[]
        {
            new ScheduleInformation { ScheduleId = "room-a@test.local", AvailabilityView = "0000" }
        };

        Assert.True(GraphRoomService.HasUsableAvailability(schedules));
    }

    [Fact]
    public void Availability_WhenSomeRoomsAreReadable_IsUsable()
    {
        var schedules = new[]
        {
            new ScheduleInformation { ScheduleId = "room-a@test.local", AvailabilityView = "0000", Error = new FreeBusyError() },
            new ScheduleInformation { ScheduleId = "room-b@test.local", AvailabilityView = "0200" }
        };

        Assert.True(GraphRoomService.HasUsableAvailability(schedules));
    }

    [Fact]
    public void Availability_EmptyResponse_CountsAsNoData()
    {
        Assert.False(GraphRoomService.HasUsableAvailability(Array.Empty<ScheduleInformation>()));
    }

    [Theory]
    [InlineData("0000", false)]
    [InlineData("0002", true)]
    [InlineData("2000", true)]
    [InlineData("", false)]
    public void AvailabilityView_BusyDetection(string view, bool expectedBusy)
    {
        Assert.Equal(expectedBusy, GraphRoomService.IsBusy(view));
    }
}
