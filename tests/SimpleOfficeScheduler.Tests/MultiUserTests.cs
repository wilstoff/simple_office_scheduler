using Microsoft.Playwright;
using Xunit;

namespace SimpleOfficeScheduler.Tests;

public class MultiUserTests : IClassFixture<PlaywrightWebAppFixture>, IAsyncLifetime
{
    private readonly PlaywrightWebAppFixture _fixture;
    private IBrowserContext _contextA = null!;
    private IBrowserContext _contextB = null!;
    private IPage _pageA = null!;
    private IPage _pageB = null!;

    public MultiUserTests(PlaywrightWebAppFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _contextA = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        _pageA = await _contextA.NewPageAsync();
        await LoginOnPage(_pageA, "testadmin");

        _contextB = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        _pageB = await _contextB.NewPageAsync();
        await LoginOnPage(_pageB, "testuser2");
    }

    public async Task DisposeAsync()
    {
        await _contextA.DisposeAsync();
        await _contextB.DisposeAsync();
    }

    private async Task LoginOnPage(IPage page, string username)
    {
        await page.GotoAsync($"{_fixture.BaseUrl}/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        var usernameField = page.Locator("input[id='username'], input[name='username']");
        if (await usernameField.CountAsync() > 0)
        {
            await usernameField.FillAsync(username);
            await page.Locator("input[id='password'], input[name='password']").FillAsync("Test123!");
            await page.Locator("button[type='submit']").ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }
    }

    private async Task CreateEventViaApi(IPage page, string title, DateTime startTime, DateTime endTime, int capacity = 3)
    {
        await page.EvaluateAsync(@$"
            fetch('/api/events', {{
                method: 'POST',
                headers: {{ 'Content-Type': 'application/json' }},
                body: JSON.stringify({{
                    title: '{title}',
                    startTime: '{startTime:yyyy-MM-ddTHH:mm:ss}',
                    endTime: '{endTime:yyyy-MM-ddTHH:mm:ss}',
                    capacity: {capacity}
                }})
            }})
        ");
    }

    private async Task<bool> WaitForEventTextOnCalendar(IPage page, string text, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var texts = await page.Locator(".fc-event").AllInnerTextsAsync();
            if (texts.Any(t => t.Contains(text)))
                return true;
            await page.WaitForTimeoutAsync(500);
        }
        return false;
    }

    private async Task NavigateBothToCalendar()
    {
        await _pageA.GotoAsync($"{_fixture.BaseUrl}/");
        await _pageB.GotoAsync($"{_fixture.BaseUrl}/");
        await _pageA.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _pageB.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _pageA.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
        await _pageB.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
    }

    [Fact]
    public async Task UserA_Creates_Event_UserB_Sees_It()
    {
        await NavigateBothToCalendar();

        var initialCount = await _pageB.Locator(".fc-timegrid-event").CountAsync();

        var today = DateTime.Now.Date;
        await CreateEventViaApi(_pageA, "Cross User Event", today.AddHours(9), today.AddHours(10));

        var eventAppeared = false;
        for (var i = 0; i < 20; i++)
        {
            await _pageB.WaitForTimeoutAsync(500);
            var currentCount = await _pageB.Locator(".fc-timegrid-event").CountAsync();
            if (currentCount > initialCount)
            {
                eventAppeared = true;
                break;
            }
        }

        Assert.True(eventAppeared,
            "User B's calendar should show the event created by User A without manual refresh");
    }

    [Fact]
    public async Task UserA_Edits_Event_Title_UserB_Sees_Updated_Title()
    {
        await NavigateBothToCalendar();

        var today = DateTime.Now.Date;
        await CreateEventViaApi(_pageA, "Original Title XU", today.AddHours(11), today.AddHours(12));
        Assert.True(await WaitForEventTextOnCalendar(_pageB, "Original Title XU"),
            "User B should see the original event");

        // Get event ID via search
        var eventId = await _pageA.EvaluateAsync<int>(@"
            (async () => {
                const r = await fetch('/api/events/search?q=Original Title XU');
                const data = await r.json();
                return data[0]?.id ?? 0;
            })()
        ");
        Assert.True(eventId > 0, "Should find the event by search");

        // Update title
        await _pageA.EvaluateAsync($@"
            fetch('/api/events/{eventId}', {{
                method: 'PUT',
                headers: {{ 'Content-Type': 'application/json' }},
                body: JSON.stringify({{
                    title: 'Updated Title XU',
                    startTime: '{today.AddHours(11):yyyy-MM-ddTHH:mm:ss}',
                    endTime: '{today.AddHours(12):yyyy-MM-ddTHH:mm:ss}',
                    capacity: 3
                }})
            }})
        ");

        Assert.True(await WaitForEventTextOnCalendar(_pageB, "Updated Title XU"),
            "User B should see the updated title");
    }

    [Fact]
    public async Task UserA_Cancels_Occurrence_UserB_Sees_Cancelled()
    {
        await NavigateBothToCalendar();

        var today = DateTime.Now.Date;
        await CreateEventViaApi(_pageA, "Cancel XU Test", today.AddHours(13), today.AddHours(14));
        Assert.True(await WaitForEventTextOnCalendar(_pageB, "Cancel XU Test"),
            "User B should see the event");

        // Get occurrence ID
        var occurrenceId = await _pageA.EvaluateAsync<int>(@"
            (async () => {
                const r = await fetch('/api/events/search?q=Cancel XU Test');
                const data = await r.json();
                return data[0]?.occurrences?.[0]?.id ?? 0;
            })()
        ");
        Assert.True(occurrenceId > 0, "Should find the occurrence");

        // Cancel the occurrence
        await _pageA.EvaluateAsync($@"
            fetch('/api/events/occurrences/{occurrenceId}/cancel', {{ method: 'POST' }})
        ");

        Assert.True(await WaitForEventTextOnCalendar(_pageB, "CANCELLED"),
            "User B should see CANCELLED on the event");
    }

    [Fact]
    public async Task UserA_Signs_Up_UserB_Sees_Signup_Count_Change()
    {
        await NavigateBothToCalendar();

        var today = DateTime.Now.Date;
        // User B creates event with capacity 5
        await CreateEventViaApi(_pageB, "Signup XU Test", today.AddHours(15), today.AddHours(16), 5);
        Assert.True(await WaitForEventTextOnCalendar(_pageA, "Signup XU Test"),
            "User A should see the event");

        // Verify initial count is 0/5
        Assert.True(await WaitForEventTextOnCalendar(_pageB, "0/5"),
            "User B should see 0/5 initially");

        // Get IDs for signup
        var ids = await _pageA.EvaluateAsync<string>(@"
            (async () => {
                const r = await fetch('/api/events/search?q=Signup XU Test');
                const data = await r.json();
                return JSON.stringify({ eventId: data[0]?.id, occId: data[0]?.occurrences?.[0]?.id });
            })()
        ");
        var parsed = System.Text.Json.JsonDocument.Parse(ids);
        var eventId = parsed.RootElement.GetProperty("eventId").GetInt32();
        var occId = parsed.RootElement.GetProperty("occId").GetInt32();

        // User A signs up
        await _pageA.EvaluateAsync($@"
            fetch('/api/events/{eventId}/signup/{occId}', {{
                method: 'POST',
                headers: {{ 'Content-Type': 'application/json' }},
                body: JSON.stringify({{}})
            }})
        ");

        Assert.True(await WaitForEventTextOnCalendar(_pageB, "1/5"),
            "User B should see signup count change from 0/5 to 1/5");
    }

    [Fact]
    public async Task Users_Have_Independent_Theme_Settings()
    {
        // User A sets light theme
        await _pageA.GotoAsync($"{_fixture.BaseUrl}/");
        await _pageA.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _pageA.EvaluateAsync(@"
            fetch('/api/user/settings/theme', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ theme: 'light' })
            })
        ");
        await _pageA.EvaluateAsync("window.setTheme('light')");

        // User B keeps dark (default)
        await _pageB.GotoAsync($"{_fixture.BaseUrl}/");
        await _pageB.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Reload User A to get DB-backed theme
        await _pageA.ReloadAsync();
        await _pageA.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // User A should be light
        var themeA = await _pageA.EvaluateAsync<string?>(
            "document.documentElement.getAttribute('data-theme')");
        Assert.Equal("light", themeA);

        // User B should be dark (no data-theme attribute)
        var themeB = await _pageB.EvaluateAsync<string?>(
            "document.documentElement.getAttribute('data-theme')");
        Assert.Null(themeB);
    }
}
