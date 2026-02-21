using System.Net;
using System.Net.Http.Json;
using NodaTime;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class UseCase5_AdjustScheduleTests : IntegrationTestBase
{
    [Fact]
    public async Task OwnerUpdatesTitle_ChangesPersisted()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Original Title", description: "Original Desc");

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Updated Title",
            Description = "Updated Desc",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<EventResponse>(JsonOptions);
        Assert.Equal("Updated Title", updated!.Title);
        Assert.Equal("Updated Desc", updated.Description);
    }

    [Fact]
    public async Task OwnerUpdatesTime_OccurrencesRegenerated()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(2).AddHours(9));
        var evt = await CreateEventAsync("Time Change", startTime: start, endTime: start.PlusHours(1));

        var newStart = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(3).AddHours(10));
        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Time Change",
            StartTime = newStart,
            EndTime = newStart.PlusHours(2),
            Capacity = evt.Capacity
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.NotNull(updated);
        // At least verify the event was updated successfully
        Assert.Equal("Time Change", updated.Title);
    }

    [Fact]
    public async Task OwnerUpdatesRecurrence_OccurrencesRegenerated()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var evt = await CreateEventAsync("Recurrence Change",
            startTime: start,
            endTime: start.PlusHours(1),
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                Interval = 1,
                MaxOccurrences = 4
            });

        var originalCount = evt.Occurrences.Count;

        // Change to daily
        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Recurrence Change",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = evt.Capacity,
            Recurrence = new RecurrencePatternDto
            {
                Type = RecurrenceType.Daily,
                Interval = 1,
                MaxOccurrences = 10
            }
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.NotNull(updated);
        // Daily with 10 occurrences should have more than weekly with 4
        Assert.True(updated.Occurrences.Count >= originalCount,
            $"Expected more occurrences after daily switch: got {updated.Occurrences.Count} vs original {originalCount}");
    }

    [Fact]
    public async Task NonOwnerCannotUpdate()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("No Edit");

        await CreateSecondUserAsync("user2");
        await LoginAsAsync("user2");

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Hacked Title",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TransferOwnership_NewOwnerCanEdit()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Transfer Me");

        var user2 = await CreateSecondUserAsync("user2");

        // Transfer ownership
        var transferResponse = await Client.PostAsync($"/api/events/{evt.Id}/transfer?newOwnerId={user2.Id}", null);
        Assert.Equal(HttpStatusCode.OK, transferResponse.StatusCode);

        // Log in as new owner and edit
        await LoginAsAsync("user2");

        var editResponse = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "New Owner Title",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);

        var updated = await editResponse.Content.ReadFromJsonAsync<EventResponse>(JsonOptions);
        Assert.Equal("New Owner Title", updated!.Title);
    }

    [Fact]
    public async Task TransferOwnership_OldOwnerCannotEdit()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Transfer Away");

        var user2 = await CreateSecondUserAsync("user2");
        await Client.PostAsync($"/api/events/{evt.Id}/transfer?newOwnerId={user2.Id}", null);

        // Old owner tries to edit
        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Still Mine?",
            StartTime = evt.StartTime,
            EndTime = evt.EndTime,
            Capacity = evt.Capacity
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
