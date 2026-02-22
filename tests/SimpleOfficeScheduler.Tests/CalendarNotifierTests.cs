using Microsoft.Extensions.DependencyInjection;
using SimpleOfficeScheduler.Services;
using Xunit;

namespace SimpleOfficeScheduler.Tests;

public class CalendarNotifierTests : IntegrationTestBase
{
    [Fact]
    public async Task Creating_Event_Triggers_Calendar_Notification()
    {
        var notifier = Factory.Services.GetRequiredService<CalendarUpdateNotifier>();
        var tcs = new TaskCompletionSource();
        using var sub = notifier.Subscribe(() => tcs.TrySetResult());

        await LoginAsync();
        await CreateEventAsync("Notification Test");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public async Task Signing_Up_Triggers_Calendar_Notification()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Signup Notification Test", capacity: 5);
        var occurrenceId = evt.Occurrences.First().Id;

        // Create and login as second user
        await CreateSecondUserAsync("notifyuser2");
        await LoginAsAsync("notifyuser2");

        var notifier = Factory.Services.GetRequiredService<CalendarUpdateNotifier>();
        var tcs = new TaskCompletionSource();
        using var sub = notifier.Subscribe(() => tcs.TrySetResult());

        // Sign up
        var response = await Client.PostAsync($"/api/events/{evt.Id}/signup/{occurrenceId}", null);
        response.EnsureSuccessStatusCode();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        Assert.Same(tcs.Task, completed);
    }
}
