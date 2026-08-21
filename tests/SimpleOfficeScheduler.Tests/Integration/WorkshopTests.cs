using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class WorkshopTests : IntegrationTestBase
{
    private async Task<Event> LoadEventAsync(int eventId)
    {
        var dbFactory = Factory.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Events
            .Include(e => e.CoOwners)
            .Include(e => e.Occurrences)
            .FirstAsync(e => e.Id == eventId);
    }

    [Fact]
    public async Task CreateWorkshop_ReturnsWorkshopEventType()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Kubernetes Workshop", eventType: EventType.Workshop);

        Assert.Equal(EventType.Workshop, evt.EventType);
    }

    [Fact]
    public async Task CreateWorkshop_PersistsCoOwners()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        Assert.Contains(evt.CoOwners, o => o.UserId == user2.Id);

        var stored = await LoadEventAsync(evt.Id);
        Assert.Contains(stored.CoOwners, o => o.UserId == user2.Id);
    }

    [Fact]
    public async Task CreateWorkshop_CreatesGraphSeriesBeforeAnySignup()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        var stored = await LoadEventAsync(evt.Id);
        Assert.False(string.IsNullOrEmpty(stored.GraphSeriesId));
        Assert.All(evt.Occurrences, o => Assert.Empty(o.Signups));
    }

    [Fact]
    public async Task CreateOfficeHours_DoesNotCreateGraphSeries()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Office Hours", eventType: EventType.OfficeHours);

        var stored = await LoadEventAsync(evt.Id);
        Assert.Null(stored.GraphSeriesId);
    }

    [Fact]
    public async Task Workshop_CoOwnersDoNotConsumeCapacity()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync("user2");
        var user3 = await CreateSecondUserAsync("user3");

        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            capacity: 2,
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id, user3.Id });
        var occurrenceId = evt.Occurrences[0].Id;

        await CreateSecondUserAsync("attendee1");
        await CreateSecondUserAsync("attendee2");
        await CreateSecondUserAsync("attendee3");

        // Creator plus two co-owners are all on the meeting, but capacity 2 still allows 2 signups.
        await LoginAsAsync("attendee1");
        Assert.Equal(HttpStatusCode.OK,
            (await SignUpForOccurrenceAsync(evt.Id, occurrenceId)).StatusCode);

        await LoginAsAsync("attendee2");
        Assert.Equal(HttpStatusCode.OK,
            (await SignUpForOccurrenceAsync(evt.Id, occurrenceId)).StatusCode);

        await LoginAsAsync("attendee3");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SignUpForOccurrenceAsync(evt.Id, occurrenceId)).StatusCode);
    }

    [Fact]
    public async Task Workshop_CoOwnerCanUpdateEvent()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();
        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        await LoginAsAsync("user2");
        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Renamed By Co-Owner",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity,
            TimeZoneId = evt.TimeZoneId
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal("Renamed By Co-Owner", updated!.Title);
    }

    [Fact]
    public async Task Workshop_CoOwnerCanCancelOccurrence()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();
        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        await LoginAsAsync("user2");
        var response = await Client.PostAsync(
            $"/api/events/occurrences/{evt.Occurrences[0].Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Workshop_CoOwnerCanSetCoOwners()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync("user2");
        var user3 = await CreateSecondUserAsync("user3");
        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        await LoginAsAsync("user2");
        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/co-owners",
            new { userIds = new[] { user2.Id, user3.Id } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadEventAsync(evt.Id);
        Assert.Equal(2, stored.CoOwners.Count);
        Assert.Contains(stored.CoOwners, o => o.UserId == user3.Id);
    }

    [Fact]
    public async Task Workshop_NonOwnerCannotUpdateEvent()
    {
        await LoginAsync();
        await CreateSecondUserAsync("outsider");
        var evt = await CreateEventAsync("Kubernetes Workshop", eventType: EventType.Workshop);

        await LoginAsAsync("outsider");
        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Hijacked",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity,
            TimeZoneId = evt.TimeZoneId
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Workshop_NonOwnerCannotSetCoOwners()
    {
        await LoginAsync();
        var outsider = await CreateSecondUserAsync("outsider");
        var evt = await CreateEventAsync("Kubernetes Workshop", eventType: EventType.Workshop);

        await LoginAsAsync("outsider");
        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/co-owners",
            new { userIds = new[] { outsider.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetCoOwners_IncludingCreator_Fails()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();
        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        // The creator is always an owner and must not appear in the co-owner list.
        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/co-owners",
            new { userIds = new[] { user2.Id, evt.OwnerUserId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetCoOwners_EmptyList_LeavesCreatorAsSoleOwner()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();
        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        var response = await Client.PostAsJsonAsync($"/api/events/{evt.Id}/co-owners",
            new { userIds = Array.Empty<int>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = await LoadEventAsync(evt.Id);
        Assert.Empty(stored.CoOwners);
    }

    [Fact]
    public async Task Workshop_SignUpWithEmptyMessage_Succeeds()
    {
        await LoginAsync();
        await CreateSecondUserAsync("attendee1");
        var evt = await CreateEventAsync("Kubernetes Workshop", eventType: EventType.Workshop);

        await LoginAsAsync("attendee1");
        var response = await SignUpForOccurrenceAsync(evt.Id, evt.Occurrences[0].Id, message: "");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OfficeHours_SignUpWithEmptyMessage_StillFails()
    {
        await LoginAsync();
        await CreateSecondUserAsync("attendee1");
        var evt = await CreateEventAsync("Office Hours", eventType: EventType.OfficeHours);

        await LoginAsAsync("attendee1");
        var response = await SignUpForOccurrenceAsync(evt.Id, evt.Occurrences[0].Id, message: "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Workshop_CancellingLastSignup_KeepsGraphSeries()
    {
        await LoginAsync();
        await CreateSecondUserAsync("attendee1");
        var evt = await CreateEventAsync("Kubernetes Workshop", eventType: EventType.Workshop);
        var occurrenceId = evt.Occurrences[0].Id;

        await LoginAsAsync("attendee1");
        await SignUpForOccurrenceAsync(evt.Id, occurrenceId, "Interested");
        var response = await Client.DeleteAsync($"/api/events/{evt.Id}/signup/{occurrenceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await LoadEventAsync(evt.Id);
        Assert.False(string.IsNullOrEmpty(stored.GraphSeriesId));
    }

    [Fact]
    public async Task Workshop_MaxOccurrences_IsRejected()
    {
        await LoginAsync();

        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var response = await Client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = "Kubernetes Workshop",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 5,
            EventType = EventType.Workshop,
            Recurrence = new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                Interval = 1,
                MaxOccurrences = 4
            }
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> GetCalendarEntryAsync(string title)
    {
        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var match = doc.RootElement.EnumerateArray()
            .First(e => e.GetProperty("title").GetString()!.Contains(title));
        return match.Clone();
    }

    [Fact]
    public async Task Workshop_CalendarColorIsCyan()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        await CreateEventAsync("Cyan Workshop", startTime: start, endTime: start.PlusHours(1),
            capacity: 5, eventType: EventType.Workshop);

        var entry = await GetCalendarEntryAsync("Cyan Workshop");

        Assert.Equal("#0dcaf0", entry.GetProperty("color").GetString());
        Assert.Equal("Workshop", entry.GetProperty("extendedProps").GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task Workshop_CalendarColorTurnsAmberWhenFull()
    {
        await LoginAsync();
        await CreateSecondUserAsync("attendee1");
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var evt = await CreateEventAsync("Full Workshop", startTime: start, endTime: start.PlusHours(1),
            capacity: 1, eventType: EventType.Workshop);

        await LoginAsAsync("attendee1");
        await SignUpForOccurrenceAsync(evt.Id, evt.Occurrences[0].Id);

        var entry = await GetCalendarEntryAsync("Full Workshop");

        Assert.Equal("#ffc107", entry.GetProperty("color").GetString());
    }

    [Fact]
    public async Task Workshop_CalendarFeedListsTheOwningTeam()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        await CreateEventAsync("Team Workshop", startTime: start, endTime: start.PlusHours(1),
            eventType: EventType.Workshop, coOwnerIds: new[] { user2.Id });

        var entry = await GetCalendarEntryAsync("Team Workshop");

        var owners = entry.GetProperty("extendedProps").GetProperty("owners")
            .EnumerateArray().Select(o => o.GetString()).ToList();
        Assert.Equal(2, owners.Count);
        Assert.Contains(user2.DisplayName, owners);
    }

    [Fact]
    public async Task Workshop_CoOwnerCanEditOccurrenceName()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();
        var evt = await CreateEventAsync(
            "Kubernetes Workshop",
            eventType: EventType.Workshop,
            coOwnerIds: new[] { user2.Id });

        await LoginAsAsync("user2");
        var response = await Client.PatchAsJsonAsync(
            $"/api/events/occurrences/{evt.Occurrences[0].Id}/name",
            new { nameSuffix = "Networking Deep Dive" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await LoginAsync();
        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal("Kubernetes Workshop: Networking Deep Dive", updated!.Occurrences[0].DisplayName);
    }
}
