using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// A room is chosen on the event and booked as a resource attendee on whatever Graph object backs
/// it. Booking is asynchronous: the room mailbox accepts or declines after the fact, so the app
/// records a per-occurrence status and surfaces declines rather than assuming success.
/// </summary>
public class RoomBookingTests : IntegrationTestBase
{
    private async Task<Event> LoadEventAsync(int eventId)
    {
        var dbFactory = Factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Events
            .Include(e => e.Occurrences)
            .FirstAsync(e => e.Id == eventId);
    }

    [Fact]
    public async Task RoomsEndpoint_ReturnsConfiguredRooms()
    {
        await LoginAsync();

        var response = await Client.GetAsync("/api/rooms");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rooms = doc.RootElement.EnumerateArray().ToList();
        Assert.NotEmpty(rooms);
        Assert.Contains(rooms, r => r.GetProperty("displayName").GetString() == "Test Room A");
    }

    [Fact]
    public async Task RoomsEndpoint_RequiresAuth()
    {
        var response = await Client.GetAsync("/api/rooms");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateWorkshopWithRoom_PersistsTheRoomAndMarksOccurrencesPending()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Room Workshop", eventType: EventType.Workshop,
            roomEmail: "room-a@test.local");

        Assert.Equal("room-a@test.local", evt.RoomEmail);
        Assert.Equal("Test Room A", evt.RoomDisplayName);

        var stored = await LoadEventAsync(evt.Id);
        Assert.All(stored.Occurrences, o =>
            Assert.Equal(RoomBookingStatus.Pending, o.RoomBookingStatus));
    }

    [Fact]
    public async Task CreateWorkshopWithoutRoom_LeavesOccurrencesWithNoBookingStatus()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("No Room Workshop", eventType: EventType.Workshop);

        var stored = await LoadEventAsync(evt.Id);
        Assert.All(stored.Occurrences, o =>
            Assert.Equal(RoomBookingStatus.None, o.RoomBookingStatus));
    }

    [Fact]
    public async Task CreateWithUnknownRoom_ReturnsBadRequest()
    {
        await LoginAsync();

        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var response = await Client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = "Bad Room",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 5,
            EventType = EventType.Workshop,
            RoomEmail = "does-not-exist@test.local"
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OfficeHoursCanAlsoBookARoom()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Room Office Hours", eventType: EventType.OfficeHours,
            roomEmail: "room-a@test.local");

        Assert.Equal("room-a@test.local", evt.RoomEmail);
    }

    [Fact]
    public async Task ChangingTheRoom_ResetsFutureOccurrencesToPending()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Room Workshop", eventType: EventType.Workshop,
            roomEmail: "room-a@test.local");

        // Pretend the poller already confirmed the original booking.
        var dbFactory = Factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            foreach (var occ in db.EventOccurrences.Where(o => o.EventId == evt.Id))
                occ.RoomBookingStatus = RoomBookingStatus.Booked;
            await db.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/room",
            new { roomEmail = "room-b@test.local" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await LoadEventAsync(evt.Id);
        Assert.Equal("room-b@test.local", stored.RoomEmail);
        Assert.Equal("Test Room B", stored.RoomDisplayName);
        Assert.All(stored.Occurrences, o =>
            Assert.Equal(RoomBookingStatus.Pending, o.RoomBookingStatus));
    }

    [Fact]
    public async Task ClearingTheRoom_LeavesNoBookingStatus()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Room Workshop", eventType: EventType.Workshop,
            roomEmail: "room-a@test.local");

        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/room",
            new { roomEmail = (string?)null });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await LoadEventAsync(evt.Id);
        Assert.Null(stored.RoomEmail);
        Assert.All(stored.Occurrences, o =>
            Assert.Equal(RoomBookingStatus.None, o.RoomBookingStatus));
    }

    [Fact]
    public async Task NonOwnerCannotChangeTheRoom()
    {
        await LoginAsync();
        await CreateSecondUserAsync("outsider");
        var evt = await CreateEventAsync("Room Workshop", eventType: EventType.Workshop,
            roomEmail: "room-a@test.local");

        await LoginAsAsync("outsider");
        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/room",
            new { roomEmail = "room-b@test.local" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CoOwnerCanChangeTheRoom()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync("user2");
        var evt = await CreateEventAsync("Room Workshop", eventType: EventType.Workshop,
            roomEmail: "room-a@test.local", coOwnerIds: new[] { user2.Id });

        await LoginAsAsync("user2");
        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/room",
            new { roomEmail = "room-b@test.local" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DeclinedBooking_IsReportedOnTheEvent()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Room Workshop", eventType: EventType.Workshop,
            roomEmail: "room-a@test.local");

        var dbFactory = Factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var occ = db.EventOccurrences.First(o => o.EventId == evt.Id);
            occ.RoomBookingStatus = RoomBookingStatus.Declined;
            occ.RoomBookingError = "Room is already booked.";
            await db.SaveChangesAsync();
        }

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);

        var declined = updated!.Occurrences.Where(o => o.RoomBookingStatus == RoomBookingStatus.Declined).ToList();
        Assert.Single(declined);
        Assert.Equal("Room is already booked.", declined[0].RoomBookingError);
    }

    [Fact]
    public async Task CalendarFeed_IncludesTheRoomName()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        await CreateEventAsync("Room Feed Workshop", startTime: start, endTime: start.PlusHours(1),
            eventType: EventType.Workshop, roomEmail: "room-a@test.local");

        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");
        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("title").GetString()!.Contains("Room Feed Workshop"));

        Assert.Equal("Test Room A", entry.GetProperty("extendedProps").GetProperty("room").GetString());
    }
}
