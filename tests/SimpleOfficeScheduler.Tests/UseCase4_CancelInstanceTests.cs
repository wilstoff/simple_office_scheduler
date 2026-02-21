using System.Net;
using System.Net.Http.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class UseCase4_CancelInstanceTests : IntegrationTestBase
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

    private class ErrorResponse
    {
        public string? Error { get; set; }
    }
}
