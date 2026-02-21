using System.Net;
using System.Net.Http.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class UseCase1_CreateEventTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateSingleEvent_ReturnsEventWithOneOccurrence()
    {
        await LoginAsync();

        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var end = start.PlusHours(1);

        var evt = await CreateEventAsync("Office Hours", "Weekly meeting", start, end, capacity: 3);

        Assert.Equal("Office Hours", evt.Title);
        Assert.Equal("Weekly meeting", evt.Description);
        Assert.Equal(3, evt.Capacity);
        Assert.Single(evt.Occurrences);
        Assert.Equal("Test Admin", evt.OwnerDisplayName);
    }

    [Fact]
    public async Task CreateRecurringWeeklyEvent_ReturnsMultipleOccurrences()
    {
        await LoginAsync();

        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var end = start.PlusHours(1);

        var evt = await CreateEventAsync(
            "Weekly Standup",
            capacity: 10,
            startTime: start,
            endTime: end,
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                Interval = 1,
                MaxOccurrences = 5
            });

        Assert.Equal("Weekly Standup", evt.Title);
        Assert.NotNull(evt.Recurrence);
        Assert.Equal(RecurrenceType.Weekly, evt.Recurrence.Type);
        Assert.True(evt.Occurrences.Count > 1, $"Expected multiple occurrences but got {evt.Occurrences.Count}");
    }

    [Fact]
    public async Task CreateEvent_CapacityIsStoredCorrectly()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("Limited Event", capacity: 2);

        Assert.Equal(2, evt.Capacity);
    }

    [Fact]
    public async Task CreateEvent_OwnerIsCreatingUser()
    {
        await LoginAsync();

        var evt = await CreateEventAsync("My Event");

        Assert.Equal("Test Admin", evt.OwnerDisplayName);
        Assert.True(evt.OwnerUserId > 0);
    }

    [Fact]
    public async Task CreateEvent_WithoutLogin_ReturnsUnauthorized()
    {
        var start = new LocalDateTime(2026, 7, 1, 9, 0, 0);
        var end = start.PlusHours(1);

        var response = await Client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = "Should Fail",
            StartTime = start,
            EndTime = end,
            Capacity = 1
        }, JsonOptions);

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Redirect);
    }
}

/// <summary>
/// Extension to convert NodaTime IsoDayOfWeek to System.DayOfWeek in tests.
/// </summary>
internal static class TestIsoDayOfWeekExtensions
{
    public static DayOfWeek ToDayOfWeek(this IsoDayOfWeek isoDow) => isoDow switch
    {
        IsoDayOfWeek.Monday => DayOfWeek.Monday,
        IsoDayOfWeek.Tuesday => DayOfWeek.Tuesday,
        IsoDayOfWeek.Wednesday => DayOfWeek.Wednesday,
        IsoDayOfWeek.Thursday => DayOfWeek.Thursday,
        IsoDayOfWeek.Friday => DayOfWeek.Friday,
        IsoDayOfWeek.Saturday => DayOfWeek.Saturday,
        IsoDayOfWeek.Sunday => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(isoDow))
    };
}
