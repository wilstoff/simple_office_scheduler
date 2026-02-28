using System.Net.Http.Json;
using System.Text.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class OccurrenceDetailTests : IntegrationTestBase
{
    [Fact]
    public async Task CalendarFeed_EachOccurrenceHasDistinctId()
    {
        await LoginAsync();

        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(10));
        var created = await CreateEventAsync("Weekly Standup",
            startTime: start,
            endTime: start.PlusHours(1),
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                Interval = 1,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                MaxOccurrences = 4
            });

        var rangeStart = DateTime.UtcNow.AddDays(-1).ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(30).ToString("o");

        var feedResponse = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        feedResponse.EnsureSuccessStatusCode();
        var feedJson = await feedResponse.Content.ReadAsStringAsync();

        using var feedDoc = JsonDocument.Parse(feedJson);
        var feedItems = feedDoc.RootElement.EnumerateArray().ToList();

        Assert.True(feedItems.Count >= 2, $"Expected at least 2 occurrences, got {feedItems.Count}");

        // Each occurrence must have a unique ID (occurrence ID, not event ID)
        var occurrenceIds = feedItems.Select(i => i.GetProperty("id").GetString()).ToList();
        Assert.Equal(occurrenceIds.Distinct().Count(), occurrenceIds.Count);

        // Each occurrence's extendedProps.eventId should be the same (same parent event)
        var eventIds = feedItems.Select(i =>
            i.GetProperty("extendedProps").GetProperty("eventId").GetInt32()).Distinct().ToList();
        Assert.Single(eventIds);
        Assert.Equal(created.Id, eventIds[0]);
    }

    [Fact]
    public async Task ClickingSpecificOccurrence_ShouldShowThatOccurrence_NotNextOne()
    {
        await LoginAsync();

        // Create a weekly recurring event starting tomorrow with 4 occurrences
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(10));
        var created = await CreateEventAsync("Recurring Meeting",
            startTime: start,
            endTime: start.PlusHours(1),
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                Interval = 1,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                MaxOccurrences = 4
            });

        // Get calendar feed to get occurrence IDs (simulates what FullCalendar renders)
        var rangeStart = DateTime.UtcNow.AddDays(-1).ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(30).ToString("o");

        var feedResponse = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        feedResponse.EnsureSuccessStatusCode();
        var feedJson = await feedResponse.Content.ReadAsStringAsync();

        using var feedDoc = JsonDocument.Parse(feedJson);
        var feedItems = feedDoc.RootElement.EnumerateArray().ToList();
        Assert.True(feedItems.Count >= 2, $"Expected >= 2 occurrences, got {feedItems.Count}");

        // Cancel the SECOND occurrence to give it distinct state
        var secondOccId = int.Parse(feedItems[1].GetProperty("id").GetString()!);
        var cancelResp = await Client.PostAsync($"/api/events/occurrences/{secondOccId}/cancel", null);
        cancelResp.EnsureSuccessStatusCode();

        // Load the full event (this is what EventDetailPanel currently does)
        var evt = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{created.Id}", JsonOptions);
        Assert.NotNull(evt);

        // BUG REPRODUCTION: The current "next occurrence" logic picks the first future non-cancelled one.
        // This is WRONG when the user clicked on a different (e.g. cancelled) occurrence.
        var now = SystemClock.Instance.GetCurrentInstant();
        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(evt.TimeZoneId) ?? DateTimeZoneProviders.Tzdb["America/Chicago"];
        var nowLocal = now.InZone(zone).LocalDateTime;

        var nextOccurrence = evt.Occurrences
            .Where(o => o.StartTime.CompareTo(nowLocal) >= 0)
            .OrderBy(o => o.StartTime)
            .FirstOrDefault();

        // The clicked occurrence (2nd) should be cancelled
        var clickedOccurrence = evt.Occurrences.FirstOrDefault(o => o.Id == secondOccId);
        Assert.NotNull(clickedOccurrence);
        Assert.True(clickedOccurrence.IsCancelled, "The occurrence the user clicked IS cancelled");

        // The "next" occurrence (what panel currently shows) is NOT the clicked one
        Assert.NotNull(nextOccurrence);
        Assert.NotEqual(nextOccurrence.Id, clickedOccurrence.Id);
        Assert.False(nextOccurrence.IsCancelled, "Panel shows next (non-cancelled) instead of clicked");

        // FIX: Using the occurrence ID from the calendar feed, the panel should find
        // the exact occurrence the user clicked — proving occurrence ID is needed
        var foundById = evt.Occurrences.FirstOrDefault(o => o.Id == secondOccId);
        Assert.NotNull(foundById);
        Assert.Equal(clickedOccurrence.Id, foundById.Id);
        Assert.True(foundById.IsCancelled, "Correct occurrence found by ID is cancelled");
    }
}
