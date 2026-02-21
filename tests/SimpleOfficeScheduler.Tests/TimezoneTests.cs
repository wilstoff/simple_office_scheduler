using System.Net.Http.Json;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Tests that event times are stored and displayed as wall-clock times,
/// not shifted by UTC conversion. On a machine where local != UTC, these
/// tests will fail if .ToUniversalTime() is applied to event times.
/// </summary>
public class TimezoneTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateEvent_TimeIsNotShiftedByUtcConversion()
    {
        await LoginAsync();

        // Create event at exactly 14:00 on a specific date
        var inputStart = new DateTime(2026, 6, 15, 14, 0, 0, DateTimeKind.Unspecified);
        var inputEnd = new DateTime(2026, 6, 15, 15, 0, 0, DateTimeKind.Unspecified);

        var evt = await CreateEventAsync("2pm Meeting", startTime: inputStart, endTime: inputEnd);

        // The occurrence should be at 14:00, not shifted by the server's UTC offset
        var occurrence = evt.Occurrences.First();
        Assert.Equal(14, occurrence.StartTime.Hour);
        Assert.Equal(15, occurrence.EndTime.Hour);
        Assert.Equal(2026, occurrence.StartTime.Year);
        Assert.Equal(6, occurrence.StartTime.Month);
        Assert.Equal(15, occurrence.StartTime.Day);
    }

    [Fact]
    public async Task CalendarFeed_ReturnsWallClockTimes()
    {
        await LoginAsync();

        var inputStart = new DateTime(2026, 6, 15, 9, 30, 0, DateTimeKind.Unspecified);
        var inputEnd = new DateTime(2026, 6, 15, 10, 30, 0, DateTimeKind.Unspecified);

        await CreateEventAsync("Morning Standup", startTime: inputStart, endTime: inputEnd);

        // Query the calendar feed for that date range
        var rangeStart = new DateTime(2026, 6, 14).ToString("o");
        var rangeEnd = new DateTime(2026, 6, 16).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        // The calendar feed should contain 09:30, not a UTC-shifted time
        Assert.Contains("Morning Standup", json);
        Assert.Contains("T09:30:00", json);
    }

    [Fact]
    public async Task UpdateEvent_TimeIsNotShiftedByUtcConversion()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Original Time");

        var newStart = new DateTime(2026, 7, 1, 16, 0, 0, DateTimeKind.Unspecified);
        var newEnd = new DateTime(2026, 7, 1, 17, 0, 0, DateTimeKind.Unspecified);

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Updated Time",
            StartTime = newStart,
            EndTime = newEnd,
            Capacity = evt.Capacity
        });
        response.EnsureSuccessStatusCode();

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}");
        Assert.Equal(16, updated!.StartTime.Hour);
        Assert.Equal(17, updated.EndTime.Hour);
    }

    [Fact]
    public async Task RecurringEvent_OccurrencesPreserveWallClockTime()
    {
        await LoginAsync();

        var inputStart = new DateTime(2026, 6, 15, 11, 0, 0, DateTimeKind.Unspecified);
        var inputEnd = new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Unspecified);

        var evt = await CreateEventAsync("Weekly at 11am",
            startTime: inputStart,
            endTime: inputEnd,
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { DayOfWeek.Monday },
                Interval = 1,
                MaxOccurrences = 3
            });

        // All occurrences should be at 11:00, not shifted
        foreach (var occ in evt.Occurrences)
        {
            Assert.Equal(11, occ.StartTime.Hour);
            Assert.Equal(12, occ.EndTime.Hour);
        }
    }
}
