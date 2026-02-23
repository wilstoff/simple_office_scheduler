using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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
    public async Task CreateEvent_EmptyTitle_ReturnsBadRequest()
    {
        await LoginAsync();

        var start = new LocalDateTime(2026, 7, 1, 9, 0, 0);
        var response = await Client.PostAsJsonAsync("/api/events", new CreateEventRequest
        {
            Title = "",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = 1
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UpdateEvent_EmptyTitle_ReturnsBadRequest()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Valid Title");

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateEvent_WithRawIsoJson_ReturnsSuccess()
    {
        await LoginAsync();

        // Simulate what curl/browser sends: raw JSON with standard ISO dates, no NodaTime serializer
        var json = """
        {
            "title": "Raw JSON Event",
            "description": "Created with plain ISO dates",
            "startTime": "2099-03-15T09:00:00",
            "endTime": "2099-03-15T10:00:00",
            "capacity": 5
        }
        """;
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await Client.PostAsync("/api/events", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var evt = await response.Content.ReadFromJsonAsync<EventResponse>(JsonOptions);
        Assert.NotNull(evt);
        Assert.Equal("Raw JSON Event", evt!.Title);
        Assert.Single(evt.Occurrences);
    }

    [Fact]
    public async Task CreateEvent_PostToBlazorPage_DoesNotReturn400()
    {
        // Simulate what happens when the Blazor form does a native HTTP POST
        // to /events/create (before the circuit connects).
        // A real browser first GETs the page (which includes an antiforgery token),
        // then submits the form with that token.
        await LoginAsync();

        // 1. GET the page to obtain the antiforgery token
        var getResponse = await Client.GetAsync("/events/create");
        var html = await getResponse.Content.ReadAsStringAsync();

        // Extract the antiforgery token from the hidden input
        var tokenMatch = Regex.Match(html, @"name=""__RequestVerificationToken""\s+value=""([^""]+)""");
        Assert.True(tokenMatch.Success, "Page should contain an antiforgery token hidden input");
        var token = tokenMatch.Groups[1].Value;

        // 2. POST the form with the antiforgery token (simulating native browser submission)
        var formData = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "eventForm",
            ["_model.Title"] = "Test Event",
            ["_model.StartTime"] = "2099-03-15T09:00",
            ["_model.EndTime"] = "2099-03-15T10:00",
            ["_model.Capacity"] = "5",
            ["_model.TimeZoneId"] = "America/New_York",
        };
        var formContent = new FormUrlEncodedContent(formData);
        var response = await Client.PostAsync("/events/create", formContent);

        // Should NOT be 400 Bad Request
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
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
