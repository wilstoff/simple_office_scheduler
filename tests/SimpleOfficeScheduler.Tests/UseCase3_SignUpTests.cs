using System.Net;
using System.Net.Http.Json;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class UseCase3_SignUpTests : IntegrationTestBase
{
    [Fact]
    public async Task SignUp_Success_IncreasesSignupCount()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Sign Up Event", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        var response = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify signup count
        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal(1, updated!.Occurrences.First().SignupCount);
    }

    [Fact]
    public async Task SignUp_WhenFull_ReturnsBadRequest()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Full Event", capacity: 1);
        var occurrenceId = evt.Occurrences.First().Id;

        // First signup (as testadmin)
        var first = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Create second user and log in as them
        var user2 = await CreateSecondUserAsync("user2");
        await LoginAsAsync("user2");

        // Second signup should fail (capacity = 1)
        var second = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("full", body!.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignUp_Duplicate_ReturnsBadRequest()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Dup Event", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);

        var duplicate = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var body = await duplicate.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("already", body!.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignUp_CancelledOccurrence_ReturnsBadRequest()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Cancel Test", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        // Cancel the occurrence first
        await Client.PostAsync($"/api/events/occurrences/{occurrenceId}/cancel", null);

        // Create second user and try to sign up
        await CreateSecondUserAsync("user2");
        await LoginAsAsync("user2");

        var response = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Contains("cancelled", body!.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelSignUp_Success_DecreasesCount()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Cancel Signup Event", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        // Sign up
        await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);

        // Verify signed up
        var afterSignup = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal(1, afterSignup!.Occurrences.First().SignupCount);

        // Cancel sign up
        var response = await Client.DeleteAsync($"/api/events/{evt.Id}/signup/{occurrenceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify count decreased
        var afterCancel = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.Equal(0, afterCancel!.Occurrences.First().SignupCount);
    }

    private class ErrorResponse
    {
        public string? Error { get; set; }
    }
}
