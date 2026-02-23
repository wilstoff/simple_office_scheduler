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
