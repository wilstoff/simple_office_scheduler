using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NodaTime;
using NodaTime.Testing;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Fixture that overrides IClock with a FakeClock at a cross-date-boundary instant:
/// 3am UTC June 15, 2026 = 11pm ET June 14, 2026.
/// This lets us prove the UI uses the user's timezone (not the server's) for defaults.
/// </summary>
public class TimezonePlaywrightFixture : PlaywrightWebAppFixture
{
    public static readonly Instant FixedNow = Instant.FromUtc(2026, 6, 15, 3, 0);

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        var existing = services.Single(d => d.ServiceType == typeof(NodaTime.IClock));
        services.Remove(existing);
        services.AddSingleton<NodaTime.IClock>(_ => new FakeClock(FixedNow));
    }
}

/// <summary>
/// Playwright tests that validate event creation default times use the user's timezone,
/// not the server's clock. Uses a FakeClock set to a cross-date-boundary instant where
/// UTC and America/New_York are on different calendar dates.
/// </summary>
public class TimezoneUITests : IClassFixture<TimezonePlaywrightFixture>, IAsyncLifetime
{
    private readonly TimezonePlaywrightFixture _fixture;
    private IPage _page = null!;
    private IBrowserContext _context = null!;

    public TimezoneUITests(TimezonePlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            StorageState = _fixture.AuthState
        });
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task Calendar_TimeSlotSelection_PopulatesPanelWithWallClockTime()
    {
        // Use a browser context with a timezone far from the server's to expose the bug:
        // FullCalendar sends info.startStr with the browser's UTC offset (e.g., +09:00).
        // If DateTime.Parse on the server converts based on that offset, the panel
        // shows a completely different time instead of the wall-clock time the user clicked.
        var tzContext = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            StorageState = _fixture.AuthState,
            TimezoneId = "Asia/Tokyo" // UTC+9, far from any US timezone
        });
        var tzPage = await tzContext.NewPageAsync();

        try
        {
            await tzPage.GotoAsync($"{_fixture.BaseUrl}/");
            await tzPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await tzPage.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });

            // Click the 9:00 AM time slot on the calendar to trigger a selection
            var slot = tzPage.Locator("td.fc-timegrid-slot-lane[data-time='09:00:00']").First;
            await slot.ClickAsync();

            // Wait for the side panel to appear
            await tzPage.WaitForSelectorAsync(".side-panel.show", new() { Timeout = 5000 });

            // Read the start time from the panel's datetime-local input
            var startInput = tzPage.Locator(".side-panel input[type='datetime-local']").First;
            var startValue = await startInput.InputValueAsync();

            // The user clicked 9:00 AM on the calendar grid.
            // The panel should show 09:00, not 15:00 (UTC conversion of 9am CST).
            Assert.True(startValue.Contains("T09:"),
                $"Panel start time should be 09:xx (wall-clock time clicked), but got: {startValue}");
        }
        finally
        {
            await tzContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task EventCreate_DefaultDate_UsesUserTimezone_NotServerUtc()
    {
        // Set user's timezone preference to America/New_York via API
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await _page.EvaluateAsync(@"
            fetch('/api/user/settings/timezone', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ timeZoneId: 'America/New_York' })
            })
        ");

        // Navigate to create event page
        await _page.GotoAsync($"{_fixture.BaseUrl}/events/create");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("#startTime", new() { Timeout = 10000 });

        // Read the default start time value from the form
        var startValue = await _page.Locator("#startTime").InputValueAsync();

        // Server clock is 3am UTC June 15 → server's "tomorrow" = June 16
        // User's timezone is America/New_York where it's 11pm June 14 → user's "tomorrow" = June 15
        // The form should show June 15 (user's tomorrow), NOT June 16 (server's tomorrow)
        Assert.True(startValue.StartsWith("2026-06-15"),
            $"Default start date should be 2026-06-15 (tomorrow in New York), but got: {startValue}");
    }
}
