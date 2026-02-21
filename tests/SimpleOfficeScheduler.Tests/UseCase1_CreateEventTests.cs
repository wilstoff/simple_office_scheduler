using System.Net;
using System.Net.Http.Json;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class UseCase1_CreateEventTests : IntegrationTestBase
{
    [Fact]
    public async Task CreateSingleEvent_ReturnsEventWithOneOccurrence()
    {
        await LoginAsync();

        var start = DateTime.UtcNow.AddDays(1);
        var end = start.AddHours(1);

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

        var start = DateTime.UtcNow.AddDays(1);
        var end = start.AddHours(1);

        var evt = await CreateEventAsync(
            "Weekly Standup",
            capacity: 10,
            startTime: start,
            endTime: end,
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek },
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
        var response = await Client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = "Should Fail",
            StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddHours(1),
            Capacity = 1
        });

        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Redirect);
    }
}
