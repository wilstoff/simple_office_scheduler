using System.Net.Http.Json;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class UseCase2_ViewEventsTests : IntegrationTestBase
{
    [Fact]
    public async Task SearchByTitle_FindsMatchingEvents()
    {
        await LoginAsync();
        await CreateEventAsync("Team Sync");
        await CreateEventAsync("Code Review");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search?q=Team");

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

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search?q=budget");

        Assert.NotNull(results);
        Assert.Single(results);
        Assert.Equal("Meeting A", results[0].Title);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        await LoginAsync();
        await CreateEventAsync("Something");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search?q=nonexistent");

        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public async Task Search_NoQuery_ReturnsAllEvents()
    {
        await LoginAsync();
        await CreateEventAsync("Event 1");
        await CreateEventAsync("Event 2");

        var results = await Client.GetFromJsonAsync<List<EventResponse>>("/api/events/search");

        Assert.NotNull(results);
        Assert.True(results.Count >= 2);
    }

    [Fact]
    public async Task GetEventById_ReturnsFullDetail()
    {
        await LoginAsync();
        var created = await CreateEventAsync("Detail Test", description: "Desc");

        var evt = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{created.Id}");

        Assert.NotNull(evt);
        Assert.Equal("Detail Test", evt.Title);
        Assert.Equal("Desc", evt.Description);
        Assert.NotEmpty(evt.Occurrences);
    }

    [Fact]
    public async Task CalendarFeed_ReturnsOccurrencesInRange()
    {
        await LoginAsync();
        var start = DateTime.UtcNow.AddDays(1);
        await CreateEventAsync("Calendar Event", startTime: start, endTime: start.AddHours(1));

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
        var farFuture = DateTime.UtcNow.AddDays(60);
        await CreateEventAsync("Far Future Event", startTime: farFuture, endTime: farFuture.AddHours(1));

        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Far Future Event", json);
    }
}
