using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class TechMeetingTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateTechMeeting_ReturnsEventTypeInResponse()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);

        Assert.Equal(EventType.TechMeeting, evt.EventType);
        Assert.Single(evt.Occurrences);
        Assert.False(evt.Occurrences[0].IsLightningTalks);
    }

    [Fact]
    public async Task CreateTechMeeting_DefaultOccurrences_NotLightningTalks()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);

        Assert.All(evt.Occurrences, o =>
        {
            Assert.False(o.IsLightningTalks);
            Assert.Empty(o.Contributors);
        });
    }

    [Fact]
    public async Task SetContributors_Endpoint_Returns200()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        var response = await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/contributors",
            new { userIds = new[] { user2.Id } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify contributors are returned in the event response
        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Single(updated!.Occurrences[0].Contributors);
        Assert.Equal(user2.Id, updated.Occurrences[0].Contributors[0].UserId);
    }

    [Fact]
    public async Task SetContributors_Unauthorized_Returns400()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        // Login as non-owner
        await LoginAsAsync(user2.Username);

        var response = await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/contributors",
            new { userIds = new[] { user2.Id } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ToggleLightningTalks_On_Returns200_RemovesContributors()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        // Assign contributor
        await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/contributors",
            new { userIds = new[] { user2.Id } });

        // Toggle lightning talks on
        var response = await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/lightning-talks",
            new { isLightningTalks = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.True(updated!.Occurrences[0].IsLightningTalks);
        Assert.Empty(updated.Occurrences[0].Contributors);
    }

    [Fact]
    public async Task SignUp_OnRegularTechMeeting_Returns400()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        var response = await SignUpForOccurrenceAsync(evt.Id, occurrenceId, "My topic");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignUp_OnLightningTalks_Returns200()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        // Toggle to lightning talks
        await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/lightning-talks",
            new { isLightningTalks = true });

        // Signup as second user
        await LoginAsAsync(user2.Username);
        var response = await SignUpForOccurrenceAsync(evt.Id, occurrenceId, "Lightning topic");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CalendarFeed_TechMeeting_ReturnsGreenColor()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);

        var startUtc = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endUtc = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var feedJson = await Client.GetStringAsync($"/api/events/calendar?start={startUtc}&end={endUtc}");
        var feed = JsonSerializer.Deserialize<JsonElement>(feedJson);

        var events = feed.EnumerateArray().ToList();
        var techEvent = events.First(e => e.GetProperty("extendedProps").GetProperty("eventType").GetString() == "TechMeeting");
        Assert.Equal("#198754", techEvent.GetProperty("color").GetString());
    }

    [Fact]
    public async Task CalendarFeed_TechMeeting_ReturnsContributorNames()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/contributors",
            new { userIds = new[] { user2.Id } });

        var startUtc = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endUtc = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var feedJson = await Client.GetStringAsync($"/api/events/calendar?start={startUtc}&end={endUtc}");
        var feed = JsonSerializer.Deserialize<JsonElement>(feedJson);

        var events = feed.EnumerateArray().ToList();
        var techEvent = events.First(e => e.GetProperty("extendedProps").GetProperty("eventType").GetString() == "TechMeeting");
        var contributors = techEvent.GetProperty("extendedProps").GetProperty("contributors").EnumerateArray().ToList();
        Assert.Single(contributors);
        Assert.Equal("User user2", contributors[0].GetString());
    }

    [Fact]
    public async Task CalendarFeed_TechMeeting_UsesDisplayName()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        // Set a custom name
        var nameResponse = await Client.PatchAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/name",
            new { namePrefix = "Sprint Review", nameSuffix = "API Design" });
        nameResponse.EnsureSuccessStatusCode();

        var startUtc = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var endUtc = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var feedJson = await Client.GetStringAsync($"/api/events/calendar?start={startUtc}&end={endUtc}");
        var feed = JsonSerializer.Deserialize<JsonElement>(feedJson);

        var events = feed.EnumerateArray().ToList();
        var techEvent = events.First(e => e.GetProperty("extendedProps").GetProperty("eventType").GetString() == "TechMeeting");
        Assert.Equal("Sprint Review: API Design", techEvent.GetProperty("title").GetString());
    }

    [Fact]
    public async Task UpdateOccurrenceName_Returns200()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        var response = await Client.PatchAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/name",
            new { namePrefix = "Sprint Review", nameSuffix = "API Design" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal("Sprint Review", updated!.Occurrences[0].NamePrefix);
        Assert.Equal("API Design", updated.Occurrences[0].NameSuffix);
        Assert.Equal("Sprint Review: API Design", updated.Occurrences[0].DisplayName);
    }

    [Fact]
    public async Task GetEvent_ReturnsContributorsInOccurrences()
    {
        await LoginAsync();
        var user2 = await CreateSecondUserAsync();

        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);
        var occurrenceId = evt.Occurrences[0].Id;

        await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occurrenceId}/contributors",
            new { userIds = new[] { user2.Id } });

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);

        Assert.Single(updated!.Occurrences[0].Contributors);
        Assert.Equal(user2.Id, updated.Occurrences[0].Contributors[0].UserId);
        Assert.Equal("User user2", updated.Occurrences[0].Contributors[0].DisplayName);
    }

    [Fact]
    public async Task UpdateEvent_CannotChangeEventType()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Office Hours", eventType: EventType.OfficeHours);

        // The UpdateEventRequest doesn't include EventType,
        // so type is preserved on update. Verify the type remains unchanged.
        var updateResponse = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new
        {
            title = "Updated Office Hours",
            startTime = evt.StartTime,
            endTime = evt.EndTime,
            capacity = evt.Capacity,
            timeZoneId = evt.TimeZoneId
        }, JsonOptions);
        updateResponse.EnsureSuccessStatusCode();

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal(EventType.OfficeHours, updated!.EventType);
    }

    // ── Reminders ───────────────────────────────────────────────────

    [Fact]
    public async Task SetReminderDefinitions_ReturnsInEventResponse()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}/reminders",
            new { names = new[] { "Recording Extension", "In Sharepoint" } });
        response.EnsureSuccessStatusCode();

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal(2, updated!.ReminderDefinitions.Count);
        Assert.Equal("Recording Extension", updated.ReminderDefinitions[0].Name);
        Assert.Equal("In Sharepoint", updated.ReminderDefinitions[1].Name);
    }

    [Fact]
    public async Task SetReminderValue_ReturnsInOccurrenceResponse()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Tech Meeting", eventType: EventType.TechMeeting);

        await Client.PutAsJsonAsync($"/api/events/{evt.Id}/reminders",
            new { names = new[] { "Recording" } });

        var withDefs = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        var defId = withDefs!.ReminderDefinitions[0].Id;
        var occId = withDefs.Occurrences[0].Id;

        var response = await Client.PostAsJsonAsync(
            $"/api/events/occurrences/{occId}/reminders/{defId}",
            new { value = true });
        response.EnsureSuccessStatusCode();

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        var reminderValues = updated!.Occurrences[0].ReminderValues;
        Assert.Single(reminderValues);
        Assert.Equal(defId, reminderValues[0].ReminderDefinitionId);
        Assert.True(reminderValues[0].Value);
    }

    [Fact]
    public async Task SetReminderDefinitions_OnOfficeHours_Returns400()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Office Hours", eventType: EventType.OfficeHours);

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}/reminders",
            new { names = new[] { "Test" } });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetEvent_ReturnsPastOccurrences()
    {
        await LoginAsync();

        // Create event with a past date
        var pastStart = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(-7).AddHours(14));
        var pastEnd = pastStart.PlusHours(1);

        var evt = await CreateEventAsync("Past TM", startTime: pastStart, endTime: pastEnd,
            eventType: EventType.TechMeeting);

        var fetched = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);

        // The past occurrence should still be returned
        Assert.Single(fetched!.Occurrences);
        Assert.Equal(pastStart, fetched.Occurrences[0].StartTime);
    }
}
