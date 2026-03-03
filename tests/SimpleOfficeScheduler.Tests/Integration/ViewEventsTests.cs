using System.Net.Http.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class ViewEventsTests : IntegrationTestBase
{
    [Fact]
    public async Task SearchByTitle_FindsMatchingEvents()
    {
        await LoginAsync();
        await CreateEventAsync("Team Sync");
        await CreateEventAsync("Code Review");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search?q=Team", JsonOptions);

        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal("Team Sync", results[0].Title);
    }

    [Fact]
    public async Task SearchByDescription_FindsMatchingEvents()
    {
        await LoginAsync();
        await CreateEventAsync("Meeting A", description: "Discuss budget planning");
        await CreateEventAsync("Meeting B", description: "Technical review");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search?q=budget", JsonOptions);

        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal("Meeting A", results[0].Title);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        await LoginAsync();
        await CreateEventAsync("Something");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search?q=nonexistent", JsonOptions);

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_NoQuery_ReturnsAllEvents()
    {
        await LoginAsync();
        await CreateEventAsync("Event 1");
        await CreateEventAsync("Event 2");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search", JsonOptions);

        Assert.NotNull(results);
        Assert.True(results.Count >= 2);
    }

    [Fact]
    public async Task GetEventById_ReturnsFullDetail()
    {
        await LoginAsync();
        var created = await CreateEventAsync("Detail Test", description: "Desc");

        var evt = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{created.Id}", JsonOptions);

        Assert.NotNull(evt);
        Assert.Equal("Detail Test", evt.Title);
        Assert.Equal("Desc", evt.Description);
        Assert.NotEmpty(evt.Occurrences);
    }

    [Fact]
    public async Task CalendarFeed_ReturnsOccurrencesInRange()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        await CreateEventAsync("Calendar Event", startTime: start, endTime: start.PlusHours(1));

        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("Calendar Event", json);
    }

    [Fact]
    public async Task CalendarFeed_ExcludesOccurrencesOutsideRange()
    {
        await LoginAsync();
        var farFuture = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(60).AddHours(9));
        await CreateEventAsync("Far Future Event", startTime: farFuture, endTime: farFuture.PlusHours(1));

        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Far Future Event", json);
    }

    [Fact]
    public async Task SearchEvents_ReturnsBothPastAndUpcomingEvents()
    {
        await LoginAsync();

        // Create an event with a future occurrence
        var futureStart = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(3).AddHours(10));
        await CreateEventAsync("Future Standup", startTime: futureStart, endTime: futureStart.PlusHours(1));

        // Create an event with a past occurrence
        var pastStart = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(-7).AddHours(10));
        await CreateEventAsync("Past Retro", startTime: pastStart, endTime: pastStart.PlusHours(1));

        // Search returns both events regardless of occurrence timing
        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search", JsonOptions);

        Assert.NotNull(results);
        Assert.Contains(results, e => e.Title == "Future Standup");
        Assert.Contains(results, e => e.Title == "Past Retro");

        // Each event has occurrences loaded
        var futureEvent = results.First(e => e.Title == "Future Standup");
        var pastEvent = results.First(e => e.Title == "Past Retro");
        Assert.NotEmpty(futureEvent.Occurrences);
        Assert.NotEmpty(pastEvent.Occurrences);

        // Verify occurrence times are correct for UI classification
        Assert.True(futureEvent.Occurrences.Any(o => o.StartTime.CompareTo(LocalDateTime.FromDateTime(DateTime.Now)) > 0),
            "Future event should have occurrence after now");
        Assert.True(pastEvent.Occurrences.All(o => o.StartTime.CompareTo(LocalDateTime.FromDateTime(DateTime.Now)) < 0),
            "Past event should have all occurrences before now");
    }
}
