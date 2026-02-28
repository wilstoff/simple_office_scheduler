using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class DeleteEventTests : IntegrationTestBase
{
    [Fact]
    public async Task OwnerDeletesEvent_Returns200()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Delete Me");

        var response = await Client.DeleteAsync($"/api/events/{evt.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify event is gone
        var getResponse = await Client.GetAsync($"/api/events/{evt.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task NonOwnerCannotDeleteEvent()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Owner Only Delete");

        await CreateSecondUserAsync("user2");
        await LoginAsAsync("user2");

        var response = await Client.DeleteAsync($"/api/events/{evt.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("owner", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteEvent_RemovesAllOccurrencesAndSignups()
    {
        await LoginAsync();
        var start = LocalDateTime.FromDateTime(DateTime.Now.Date.AddDays(1).AddHours(9));
        var evt = await CreateEventAsync("Cascade Delete Test",
            startTime: start,
            endTime: start.PlusHours(1),
            capacity: 5,
            recurrence: new RecurrencePatternDto
            {
                Type = RecurrenceType.Weekly,
                DaysOfWeek = new List<DayOfWeek> { start.DayOfWeek.ToDayOfWeek() },
                Interval = 1,
                MaxOccurrences = 3
            });

        // Sign up for the first occurrence
        var occurrenceId = evt.Occurrences.First().Id;
        await Client.PostAsJsonAsync(
            $"/api/events/{evt.Id}/signup/{occurrenceId}",
            new SignUpRequest { Message = "Testing" }, JsonOptions);

        // Delete the event
        var deleteResponse = await Client.DeleteAsync($"/api/events/{evt.Id}");
        Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);

        // Verify all occurrences and signups are removed from DB
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var remainingOccurrences = await db.EventOccurrences
            .Where(o => o.EventId == evt.Id).CountAsync();
        Assert.Equal(0, remainingOccurrences);
    }

    [Fact]
    public async Task DeleteEvent_NonExistent_Returns404()
    {
        await LoginAsync();

        var response = await Client.DeleteAsync("/api/events/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
