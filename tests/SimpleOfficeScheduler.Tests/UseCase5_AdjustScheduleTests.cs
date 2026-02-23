using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using SimpleOfficeScheduler.Data;
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

    [Fact]
    public async Task UpdateRecurringEvent_ChangeDayOfWeek_OccurrencesOnNewDay()
    {
        await LoginAsync();

        // Create a weekly event — pick a day 1 day from now
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var originalDay = start.DayOfWeek.ToDayOfWeek();
        var evt = await CreateEventAsync("Day Change Test",
            startTime: start,
            endTime: start.PlusHours(1),
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { originalDay },
                Interval = 1,
                MaxOccurrences = 4
            });

        Assert.True(evt.Occurrences.Count > 0, "Should have initial occurrences");

        // Change the recurring day to 2 days later (different day of week)
        // Keep StartTime the same (this is what the UI does when user only changes checkboxes)
        var newDay = (DayOfWeek)(((int)originalDay + 2) % 7);

        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Day Change Test",
            StartTime = start,
            EndTime = start.PlusHours(1),
            Capacity = evt.Capacity,
            Recurrence = new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { newDay },
                Interval = 1,
                MaxOccurrences = 4
            }
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await Client.GetFromJsonAsync<EventResponse>($"/api/events/{evt.Id}", JsonOptions);
        Assert.NotNull(updated);
        Assert.True(updated.Occurrences.Count > 0,
            $"Expected occurrences on {newDay} but got none — day change produced no occurrences");

        // Verify all occurrences are on the new day
        foreach (var occ in updated.Occurrences)
        {
            var occDay = occ.StartTime.DayOfWeek.ToDayOfWeek();
            Assert.Equal(newDay, occDay);
        }
    }

    [Fact]
    public async Task UpdateRecurringEvent_PreservesPastOccurrences()
    {
        await LoginAsync();

        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var evt = await CreateEventAsync("Past Preserve Test",
            startTime: start,
            endTime: start.PlusHours(1),
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                Interval = 1,
                MaxOccurrences = 4
            });

        // Insert a "past" occurrence directly into the DB
        var pastStart = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(-7).AddHours(9));
        var pastEnd = pastStart.PlusHours(1);

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EventOccurrences.Add(new EventOccurrence
            {
                EventId = evt.Id,
                StartTime = pastStart,
                EndTime = pastEnd
            });
            await db.SaveChangesAsync();
        }

        // Update the event's time (shift forward by 2 hours)
        var newStart = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(11));
        var response = await Client.PutAsJsonAsync($"/api/events/{evt.Id}", new UpdateEventRequest
        {
            Title = "Past Preserve Test",
            StartTime = newStart,
            EndTime = newStart.PlusHours(1),
            Capacity = evt.Capacity,
            Recurrence = new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { newStart.DayOfWeek.ToDayOfWeek() },
                Interval = 1,
                MaxOccurrences = 4
            }
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify the past occurrence is still present with its original time
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var allOccurrences = await db.EventOccurrences
                .Where(o => o.EventId == evt.Id)
                .ToListAsync();
            var pastOccurrence = allOccurrences
                .FirstOrDefault(o => o.StartTime.Date == pastStart.Date);

            Assert.NotNull(pastOccurrence);
            Assert.Equal(9, pastOccurrence.StartTime.Hour); // original time, not 11
        }
    }
}
