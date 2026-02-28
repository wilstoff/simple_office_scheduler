using System.Net.Http.Json;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Tests that event times are stored and displayed as wall-clock times,
/// not shifted by UTC conversion. Uses NodaTime LocalDateTime throughout.
/// </summary>
public class TimezoneTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateEvent_TimeIsNotShiftedByUtcConversion()
    {
        await LoginAsync();

        // Create event at exactly 14:00 on a specific date
        var inputStart = new LocalDateTime(2026, 6, 15, 14, 0, 0);
        var inputEnd = new LocalDateTime(2026, 6, 15, 15, 0, 0);

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
    public async Task CalendarFeed_ReturnsTimesForEvent()
    {
        await LoginAsync();

        var inputStart = new LocalDateTime(2026, 6, 15, 9, 30, 0);
        var inputEnd = new LocalDateTime(2026, 6, 15, 10, 30, 0);

        await CreateEventAsync("Morning Standup",
            startTime: inputStart,
            endTime: inputEnd,
            timeZoneId: "America/New_York");

        // Query the calendar feed for that date range
        var rangeStart = new DateTime(2026, 6, 14).ToString("o");
        var rangeEnd = new DateTime(2026, 6, 16).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        // The calendar feed should contain the event
        Assert.Contains("Morning Standup", json);
        // Calendar feed now emits UTC times for FullCalendar
        // 09:30 EDT = 13:30 UTC
        Assert.Contains("T13:30:00Z", json);
    }

    [Fact]
    public async Task UpdateEvent_TimeIsNotShiftedByUtcConversion()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Original Time");

        var newStart = new LocalDateTime(2026, 7, 1, 16, 0, 0);
        var newEnd = new LocalDateTime(2026, 7, 1, 17, 0, 0);

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Updated Time",
            StartTime = newStart,
            EndTime = newEnd,
            Capacity = evt.Capacity
        }, JsonOptions);
        response.EnsureSuccessStatusCode();

        var updated = await response.Content.ReadFromJsonAsync<EventResponse>(JsonOptions);
        Assert.Equal(16, updated!.StartTime.Hour);
        Assert.Equal(17, updated.EndTime.Hour);
    }

    [Fact]
    public async Task RecurringEvent_OccurrencesPreserveWallClockTime()
    {
        await LoginAsync();

        var inputStart = new LocalDateTime(2026, 6, 15, 11, 0, 0);
        var inputEnd = new LocalDateTime(2026, 6, 15, 12, 0, 0);

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

    [Fact]
    public async Task CreateEvent_StoresAndReturnsTimeZoneId()
    {
        await LoginAsync();

        var inputStart = new LocalDateTime(2026, 6, 15, 14, 0, 0);
        var inputEnd = new LocalDateTime(2026, 6, 15, 15, 0, 0);

        var evt = await CreateEventAsync("TZ Test",
            startTime: inputStart,
            endTime: inputEnd,
            timeZoneId: "America/Chicago");

        Assert.Equal("America/Chicago", evt.TimeZoneId);

        // Verify the occurrence also reports the timezone
        var occ = evt.Occurrences.First();
        Assert.Equal("America/Chicago", occ.TimeZoneId);
    }

    [Fact]
    public async Task CreateEvent_InvalidTimezoneDefaultsToFallback()
    {
        await LoginAsync();

        var inputStart = new LocalDateTime(2026, 6, 15, 14, 0, 0);
        var inputEnd = new LocalDateTime(2026, 6, 15, 15, 0, 0);

        var evt = await CreateEventAsync("Invalid TZ",
            startTime: inputStart,
            endTime: inputEnd,
            timeZoneId: "Invalid/Timezone");

        // Should fall back to system default, not store the invalid ID
        Assert.NotEqual("Invalid/Timezone", evt.TimeZoneId);
        Assert.NotEmpty(evt.TimeZoneId);
    }

    [Fact]
    public async Task OccurrenceResponse_IncludesUtcConversion()
    {
        await LoginAsync();

        var inputStart = new LocalDateTime(2026, 6, 15, 14, 0, 0);
        var inputEnd = new LocalDateTime(2026, 6, 15, 15, 0, 0);

        var evt = await CreateEventAsync("UTC Test",
            startTime: inputStart,
            endTime: inputEnd,
            timeZoneId: "America/New_York");

        var occ = evt.Occurrences.First();

        // 14:00 EDT (UTC-4 in June) = 18:00 UTC
        Assert.Equal(14, occ.StartTime.Hour);
        var startUtcDt = occ.StartTimeUtc.ToDateTimeUtc();
        Assert.Equal(18, startUtcDt.Hour);
    }

    [Fact]
    public void GetDefaultEventTimes_ComputesTomorrowInUserTimezone()
    {
        // 03:00 UTC on June 15 = 23:00 EDT on June 14 in New York
        var now = Instant.FromUtc(2026, 6, 15, 3, 0);

        var (start, end) = TimeZoneHelper.GetDefaultEventTimes("America/New_York", now);

        // "Tomorrow" in New York is June 15 (today there is June 14)
        Assert.Equal(new DateTime(2026, 6, 15, 9, 0, 0), start);
        Assert.Equal(new DateTime(2026, 6, 15, 10, 0, 0), end);
    }

    [Fact]
    public void GetDefaultEventTimes_ServerUtcGivesDifferentDateThanUserTimezone()
    {
        // 03:00 UTC on June 15 — same instant, different local dates
        var now = Instant.FromUtc(2026, 6, 15, 3, 0);

        var (utcStart, _) = TimeZoneHelper.GetDefaultEventTimes("Etc/UTC", now);
        var (nyStart, _) = TimeZoneHelper.GetDefaultEventTimes("America/New_York", now);

        // UTC "today" = June 15, so "tomorrow" = June 16
        Assert.Equal(16, utcStart.Day);
        // New York "today" = June 14, so "tomorrow" = June 15
        Assert.Equal(15, nyStart.Day);
    }
}
