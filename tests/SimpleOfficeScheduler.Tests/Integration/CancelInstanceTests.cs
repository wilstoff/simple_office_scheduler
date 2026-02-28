using System.Net;
using System.Net.Http.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class CancelInstanceTests : IntegrationTestBase
{
    [Fact]
    public async Task OwnerCancelsOccurrence_SetsCancelledFlag()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Cancel Instance Test", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        var response = await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify cancelled
        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.True(updated!.Occurrences.First().IsCancelled);
    }

    [Fact]
    public async Task NonOwnerCannotCancelOccurrence()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Owner Only Cancel", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        // Switch to a different user
        await CreateSecondUserAsync("user2");
        await LoginAsAsync("user2");

        var response = await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("owner", body!.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledOccurrence_ShowsInCalendarFeed()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var evt = await CreateEventAsync("Visible Cancelled", startTime: start, endTime: start.PlusHours(1));
        var occurrenceId = evt.Occurrences.First().Id;

        await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);

        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");

        var response = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Contains("Visible Cancelled", json);
        Assert.Contains("\"isCancelled\":true", json);
    }

    [Fact]
    public async Task CannotSignUpForCancelledOccurrence()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("No Signup After Cancel", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);

        var signupResponse = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, signupResponse.StatusCode);
    }

    [Fact]
    public async Task OwnerUncancelsOccurrence_ClearsCancelledFlag()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Uncancel Instance Test", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        // Cancel first
        await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);

        // Uncancel
        var response = await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/uncancel", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify restored
        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.False(updated!.Occurrences.First().IsCancelled);

        // Verify calendar feed shows it as active (not cancelled)
        var rangeStart = DateTime.UtcNow.ToString("o");
        var rangeEnd = DateTime.UtcNow.AddDays(7).ToString("o");
        var feedResponse = await Client.GetAsync($"/api/events/calendar?start={rangeStart}&end={rangeEnd}");
        var json = await feedResponse.Content.ReadAsStringAsync();
        Assert.Contains("Uncancel Instance Test", json);
        Assert.DoesNotContain("\"isCancelled\":true", json);
    }

    [Fact]
    public async Task NonOwnerCannotUncancelOccurrence()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Owner Only Uncancel", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);

        // Switch to a different user
        await CreateSecondUserAsync("user2");
        await LoginAsAsync("user2");

        var response = await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/uncancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("owner", body!.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UncancelNonCancelledOccurrence_ReturnsBadRequest()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Already Active", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        // Try to uncancel without cancelling first
        var response = await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/uncancel", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("not cancelled", body!.Error!, StringComparison.OrdinalIgnoreCase);
    }

    private class ErrorResponse
    {
        public string? Error { get; set; }
    }
}
