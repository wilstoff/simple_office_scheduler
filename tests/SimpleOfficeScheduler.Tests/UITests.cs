using Microsoft.Playwright;
using Xunit;

namespace SimpleOfficeScheduler.Tests;

public class UITests : IClassFixture<PlaywrightWebAppFixture>, IAsyncLifetime
{
    private readonly PlaywrightWebAppFixture _fixture;
    private IPage _page = null!;
    private IBrowserContext _context = null!;

    public UITests(PlaywrightWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        _page = await _context.NewPageAsync();

        // Login via the UI
        await LoginOnPage(_page);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    private async Task LoginOnPage(IPage page)
    {
        await page.GotoAsync($"{_fixture.BaseUrl}/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var usernameField = page.Locator("input[id='username'], input[name='username']");
        if (await usernameField.CountAsync() > 0)
        {
            await usernameField.FillAsync("testadmin");
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

    [Fact]
    public async Task Sidebar_Icons_Are_Centered()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Wait for scoped CSS to load (sidebar should be narrow, not full-width)
        await _page.WaitForFunctionAsync(
            "() => { var el = document.querySelector('.sidebar'); return el && el.getBoundingClientRect().width < 200; }",
            null, new() { Timeout = 10000 });

        // Get the sidebar element
        var sidebar = _page.Locator(".sidebar");
        var sidebarBox = await sidebar.BoundingBoxAsync();
        Assert.NotNull(sidebarBox);

        // Get first nav icon
        var icon = _page.Locator(".nav-item .nav-link .bi").First;
        var iconBox = await icon.BoundingBoxAsync();
        Assert.NotNull(iconBox);

        // Icon center should be near sidebar horizontal center
        var sidebarCenter = sidebarBox.X + sidebarBox.Width / 2;
        var iconCenter = iconBox.X + iconBox.Width / 2;

        // Allow 8px tolerance for centering
        Assert.True(
            Math.Abs(sidebarCenter - iconCenter) < 8,
            $"Icon center ({iconCenter:F1}) should be within 8px of sidebar center ({sidebarCenter:F1}), diff = {Math.Abs(sidebarCenter - iconCenter):F1}px");
    }

    [Fact]
    public async Task Brand_Logo_Is_Visible()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The brand logo SVG should exist and be visible
        var logo = _page.Locator(".brand-logo");
        await Assertions.Expect(logo).ToBeVisibleAsync();

        var box = await logo.BoundingBoxAsync();
        Assert.NotNull(box);

        // Logo should have reasonable dimensions (not collapsed to 0)
        Assert.True(box.Width >= 20, $"Logo width ({box.Width}) should be >= 20px");
        Assert.True(box.Height >= 20, $"Logo height ({box.Height}) should be >= 20px");
    }

    [Fact]
    public async Task Short_Event_Text_Does_Not_Overflow()
    {
        // Create a 1-hour event via API first
        var apiContext = await _fixture.Browser.NewContextAsync();
        var apiPage = await apiContext.NewPageAsync();
        await LoginOnPage(apiPage);

        var today = DateTime.Now.Date;
        await CreateEventViaApi(apiPage, "UI Test Event", today.AddHours(10), today.AddHours(11));
        await apiContext.DisposeAsync();

        // Navigate to calendar
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForTimeoutAsync(2000);

        // Find event blocks on the calendar
        var events = _page.Locator(".fc-timegrid-event");
        var eventCount = await events.CountAsync();

        if (eventCount > 0)
        {
            var firstEvent = events.First;
            var eventBox = await firstEvent.BoundingBoxAsync();
            Assert.NotNull(eventBox);

            var content = firstEvent.Locator(".fc-event-main");
            var contentBox = await content.BoundingBoxAsync();

            if (contentBox != null)
            {
                var eventBottom = eventBox.Y + eventBox.Height;
                var contentBottom = contentBox.Y + contentBox.Height;

                Assert.True(
                    contentBottom <= eventBottom + 2,
                    $"Event content bottom ({contentBottom:F1}) should not exceed event block bottom ({eventBottom:F1})");
            }
        }
    }

    [Fact]
    public async Task Short_Event_Shows_Owner_Name()
    {
        // Create a 1-hour event via API (owner will be "Test Admin")
        var apiContext = await _fixture.Browser.NewContextAsync();
        var apiPage = await apiContext.NewPageAsync();
        await LoginOnPage(apiPage);

        var today = DateTime.Now.Date;
        await CreateEventViaApi(apiPage, "Owner Visible Test", today.AddHours(10), today.AddHours(11));
        await apiContext.DisposeAsync();

        // Navigate to calendar
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForTimeoutAsync(2000);

        // Find event blocks
        var events = _page.Locator(".fc-timegrid-event");
        var eventCount = await events.CountAsync();
        Assert.True(eventCount > 0, "Expected at least one event on the calendar");

        var firstEvent = events.First;
        var eventBox = await firstEvent.BoundingBoxAsync();
        Assert.NotNull(eventBox);

        // Check that owner name text is present and visible within the event block
        var eventHtml = await firstEvent.InnerHTMLAsync();
        Assert.Contains("Test Admin", eventHtml);

        // Verify the owner text element is visible (not clipped to zero height)
        var ownerVisible = await firstEvent.EvaluateAsync<bool>(@"
            (el) => {
                const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
                while (walker.nextNode()) {
                    if (walker.currentNode.textContent.includes('Test Admin')) {
                        const range = document.createRange();
                        range.selectNodeContents(walker.currentNode);
                        const rect = range.getBoundingClientRect();
                        const eventRect = el.getBoundingClientRect();
                        // Owner text bottom must be within the event block (with 2px tolerance)
                        return rect.height > 0 && rect.bottom <= eventRect.bottom + 2;
                    }
                }
                return false;
            }
        ");

        Assert.True(ownerVisible, "Owner name 'Test Admin' should be visible within the event block (not clipped)");
    }

    [Fact]
    public async Task Sidebar_Expanded_Shows_Full_Brand_And_Saturday()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Wait for sidebar CSS to load
        await _page.WaitForFunctionAsync(
            "() => { var el = document.querySelector('.sidebar'); return el && el.getBoundingClientRect().width < 200; }",
            null, new() { Timeout = 10000 });

        // Hover over sidebar to expand it
        await _page.Locator(".sidebar").HoverAsync();
        await _page.WaitForTimeoutAsync(400); // Wait for 200ms transition + buffer

        // Assert the brand label is visible and shows full text
        var brandLabel = _page.Locator(".navbar-brand .nav-label");
        await Assertions.Expect(brandLabel).ToBeVisibleAsync();
        var brandText = await brandLabel.TextContentAsync();
        Assert.Equal("Office Scheduler", brandText?.Trim());

        // Assert the brand label is not clipped (its right edge is within the sidebar)
        var sidebarBox = await _page.Locator(".sidebar").BoundingBoxAsync();
        var labelBox = await brandLabel.BoundingBoxAsync();
        Assert.NotNull(sidebarBox);
        Assert.NotNull(labelBox);
        Assert.True(
            labelBox.X + labelBox.Width <= sidebarBox.X + sidebarBox.Width + 2,
            $"Brand label right edge ({labelBox.X + labelBox.Width:F1}) should be within sidebar ({sidebarBox.X + sidebarBox.Width:F1})");

        // Assert Saturday column is visible in the calendar while sidebar is expanded
        var colHeaders = _page.Locator(".fc-col-header-cell");
        var headerCount = await colHeaders.CountAsync();
        Assert.True(headerCount >= 7, $"Expected 7 day column headers, found {headerCount}");

        // The last column should be within viewport
        var lastHeader = colHeaders.Last;
        var lastHeaderBox = await lastHeader.BoundingBoxAsync();
        Assert.NotNull(lastHeaderBox);
        Assert.True(
            lastHeaderBox.X + lastHeaderBox.Width <= 1920 + 2,
            $"Last calendar column right edge ({lastHeaderBox.X + lastHeaderBox.Width:F1}) should be within viewport (1920px)");
        Assert.True(
            lastHeaderBox.Width >= 20,
            $"Last calendar column width ({lastHeaderBox.Width:F1}) should be at least 20px (not squished)");

        // Exercise sidebar: click "Create Event" nav link
        var createLink = _page.Locator(".nav-link", new() { HasText = "Create Event" });
        if (await createLink.CountAsync() > 0)
        {
            await createLink.ClickAsync();
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            Assert.Contains("events/create", _page.Url, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Theme_Toggle_Switches_Theme()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Initially should be dark theme (no data-theme attribute)
        var initialTheme = await _page.EvaluateAsync<string?>(
            "document.documentElement.getAttribute('data-theme')");
        Assert.Null(initialTheme);

        // Click the ACTUAL theme toggle button (not JS bypass)
        var toggleButton = _page.Locator(".theme-toggle");
        await Assertions.Expect(toggleButton).ToBeVisibleAsync();
        await toggleButton.ClickAsync();

        // Wait for theme to change to light
        await _page.WaitForFunctionAsync(
            "() => document.documentElement.getAttribute('data-theme') === 'light'",
            null, new() { Timeout = 3000 });

        var afterToggle = await _page.EvaluateAsync<string?>(
            "document.documentElement.getAttribute('data-theme')");
        Assert.Equal("light", afterToggle);

        // Click again to toggle back to dark
        await toggleButton.ClickAsync();

        await _page.WaitForFunctionAsync(
            "() => !document.documentElement.getAttribute('data-theme')",
            null, new() { Timeout = 3000 });

        var afterToggleBack = await _page.EvaluateAsync<string?>(
            "document.documentElement.getAttribute('data-theme')");
        Assert.Null(afterToggleBack);

        // Verify localStorage was updated
        var stored = await _page.EvaluateAsync<string>("localStorage.getItem('theme')");
        Assert.Equal("dark", stored);
    }

    [Fact]
    public async Task Theme_Toggle_Has_Spacing_From_Username()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        var toggleButton = _page.Locator(".theme-toggle");
        var toggleBox = await toggleButton.BoundingBoxAsync();
        Assert.NotNull(toggleBox);

        // Find the username text element
        var usernameSpan = _page.Locator(".top-row span", new() { HasText = "Test Admin" });
        var usernameBox = await usernameSpan.BoundingBoxAsync();
        Assert.NotNull(usernameBox);

        // There should be at least 8px gap between toggle button and username
        var gap = usernameBox.X - (toggleBox.X + toggleBox.Width);
        Assert.True(
            gap >= 8,
            $"Gap between theme toggle and username should be >= 8px, actual: {gap:F1}px");
    }

    [Fact]
    public async Task Calendar_Updates_In_Real_Time_When_Event_Created()
    {
        // User 1 navigates to calendar
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForTimeoutAsync(2000);

        // Record initial event count for User 1
        var initialCount = await _page.Locator(".fc-timegrid-event").CountAsync();

        // User 2: create a second browser context, login, navigate to calendar
        var context2 = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
        });
        var page2 = await context2.NewPageAsync();
        await LoginOnPage(page2);
        await page2.GotoAsync($"{_fixture.BaseUrl}/");
        await page2.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page2.WaitForTimeoutAsync(2000);

        // User 2 creates a new event via API
        var today = DateTime.Now.Date;
        await CreateEventViaApi(page2, "Real-Time Test Event", today.AddHours(14), today.AddHours(15));

        // Wait for User 1's calendar to show the new event (without manual refresh)
        var eventAppeared = false;
        for (var i = 0; i < 20; i++)
        {
            await _page.WaitForTimeoutAsync(500);
            var currentCount = await _page.Locator(".fc-timegrid-event").CountAsync();
            if (currentCount > initialCount)
            {
                eventAppeared = true;
                break;
            }
        }

        await context2.DisposeAsync();

        Assert.True(eventAppeared,
            $"User 1's calendar should show the new event created by User 2 without refresh. Initial count: {initialCount}");
    }

    /// <summary>
    /// Validates the FullCalendar DOM is fully rendered: time grid, column headers, time labels, toolbar.
    /// </summary>
    private async Task AssertCalendarFullyRendered(string context)
    {
        // 1. fc-view must exist and be visible
        var view = _page.Locator(".fc-view");
        await Assertions.Expect(view).ToBeVisibleAsync();

        // 2. View harness must have substantial height (calendar actually rendered, not empty shell)
        var harness = _page.Locator(".fc-view-harness");
        var harnessBox = await harness.BoundingBoxAsync();
        Assert.NotNull(harnessBox);
        Assert.True(harnessBox.Height > 200,
            $"{context}: fc-view-harness height should be > 200px, got {harnessBox.Height}");

        // 3. All 7 day column headers must be present
        var colHeaders = await _page.Locator(".fc-col-header-cell").CountAsync();
        Assert.True(colHeaders >= 7,
            $"{context}: Expected >= 7 column headers, got {colHeaders}");

        // 4. Time slots must be rendered (at least 7am-7pm = 13 hours of slots)
        var timeSlots = await _page.Locator(".fc-timegrid-slot").CountAsync();
        Assert.True(timeSlots > 10,
            $"{context}: Expected > 10 time grid slots, got {timeSlots}");

        // 5. Time axis labels must exist (7am, 8am, etc.)
        var timeLabels = await _page.Locator(".fc-timegrid-slot-label").CountAsync();
        Assert.True(timeLabels > 5,
            $"{context}: Expected > 5 time labels, got {timeLabels}");

        // 6. Toolbar must be rendered with title (e.g. "Feb 22 – 28, 2026")
        var toolbar = _page.Locator(".fc-toolbar-title");
        await Assertions.Expect(toolbar).ToBeVisibleAsync();
        var titleText = await toolbar.TextContentAsync();
        Assert.False(string.IsNullOrWhiteSpace(titleText),
            $"{context}: Toolbar title should not be empty");

        // 7. The fullcalendar-container must not be empty
        var containerChildCount = await _page.EvaluateAsync<int>(
            "document.getElementById('fullcalendar-container')?.children.length ?? 0");
        Assert.True(containerChildCount > 0,
            $"{context}: #fullcalendar-container should have children, got {containerChildCount}");
    }

    private async Task ClickNavLink(string text)
    {
        await _page.Locator(".sidebar").HoverAsync();
        await _page.WaitForTimeoutAsync(300);
        await _page.Locator(".nav-link", new() { HasText = text }).ClickAsync();
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    private async Task ClickBrandLink()
    {
        await _page.Locator(".sidebar").HoverAsync();
        await _page.WaitForTimeoutAsync(300);
        await _page.Locator(".navbar-brand").ClickAsync();
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
    }

    [Fact]
    public async Task Calendar_Renders_After_Navigating_Away_And_Back()
    {
        // Initial load
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
        await AssertCalendarFullyRendered("Initial load");

        // 30 rounds cycling through 6 distinct navigation patterns:
        // 1. Events → Calendar link          (standard return)
        // 2. Events → Office Scheduler brand (return via brand from different page)
        // 3. Events → Calendar → brand       (brand click while already on calendar)
        // 4. Dashboard → brand               (return via brand from dashboard)
        // 5. Events → brand → Events → Calendar (rapid multi-hop)
        // 6. Calendar → brand → brand        (double brand click on same page)
        for (var round = 0; round < 30; round++)
        {
            var pattern = round % 6;
            var label = $"Round {round + 1} pattern {pattern}";

            switch (pattern)
            {
                case 0: // Events → Calendar
                    await ClickNavLink("Events");
                    await ClickNavLink("Calendar");
                    await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
                    await AssertCalendarFullyRendered($"{label}: Events → Calendar");
                    break;

                case 1: // Events → brand
                    await ClickNavLink("Events");
                    await ClickBrandLink();
                    await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
                    await AssertCalendarFullyRendered($"{label}: Events → brand");
                    break;

                case 2: // Events → Calendar → brand (same-page brand click)
                    await ClickNavLink("Events");
                    await ClickNavLink("Calendar");
                    await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
                    await ClickBrandLink();
                    await _page.WaitForTimeoutAsync(500);
                    await AssertCalendarFullyRendered($"{label}: Events → Calendar → brand");
                    break;

                case 3: // Dashboard → brand
                    await ClickNavLink("Dashboard");
                    await ClickBrandLink();
                    await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
                    await AssertCalendarFullyRendered($"{label}: Dashboard → brand");
                    break;

                case 4: // Events → brand → Events → Calendar (rapid multi-hop)
                    await ClickNavLink("Events");
                    await ClickBrandLink();
                    await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
                    await ClickNavLink("Events");
                    await ClickNavLink("Calendar");
                    await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
                    await AssertCalendarFullyRendered($"{label}: Events → brand → Events → Calendar");
                    break;

                case 5: // brand → brand (double brand click on calendar)
                    await ClickBrandLink();
                    await _page.WaitForTimeoutAsync(500);
                    await ClickBrandLink();
                    await _page.WaitForTimeoutAsync(500);
                    await AssertCalendarFullyRendered($"{label}: brand → brand");
                    break;
            }
        }
    }

    [Fact]
    public async Task Calendar_Recovers_After_Container_Cleared_By_Enhanced_Navigation()
    {
        // Navigate to calendar, wait for full render
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
        await AssertCalendarFullyRendered("Initial load");

        // Simulate what Blazor enhanced navigation does on same-URL clicks:
        // 1. Clear the container (DOM patching replaces rich FullCalendar DOM with empty server-rendered div)
        // 2. Call checkAndRecover() (same function the enhancedload handler calls)
        // This verifies the recovery mechanism works: if the container is emptied,
        // the calendar is reinitialized from stored parameters.
        for (var round = 1; round <= 10; round++)
        {
            // ES modules are singletons — re-importing returns the same instance
            // with all module-level state (lastDotNetRef, lastOptions) intact.
            var recovered = await _page.EvaluateAsync<bool>(@"
                (async () => {
                    const container = document.getElementById('fullcalendar-container');
                    if (!container) return false;
                    container.innerHTML = '';

                    const mod = await import('/js/fullcalendar-interop.js');
                    return mod.checkAndRecover();
                })()
            ");

            Assert.True(recovered, $"Round {round}: checkAndRecover should return true after container cleared");
            await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 5000 });
            await AssertCalendarFullyRendered($"Recovery round {round}");
        }
    }

    [Fact]
    public async Task Calendar_Renders_After_Same_URL_Click()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
        await AssertCalendarFullyRendered("Initial load");

        // Click Calendar link while already on calendar (same-URL navigation)
        for (var i = 1; i <= 5; i++)
        {
            await ClickNavLink("Calendar");
            await _page.WaitForTimeoutAsync(1500);
            await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
            await AssertCalendarFullyRendered($"After Calendar click {i}");
        }

        // Click brand link while already on calendar (same-URL navigation)
        for (var i = 1; i <= 5; i++)
        {
            await ClickBrandLink();
            await _page.WaitForTimeoutAsync(1500);
            await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
            await AssertCalendarFullyRendered($"After brand click {i}");
        }

        // Alternate between Calendar and brand links
        for (var i = 1; i <= 5; i++)
        {
            await ClickNavLink("Calendar");
            await _page.WaitForTimeoutAsync(1000);
            await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
            await AssertCalendarFullyRendered($"Calendar-brand alternation {i}a");

            await ClickBrandLink();
            await _page.WaitForTimeoutAsync(1000);
            await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });
            await AssertCalendarFullyRendered($"Calendar-brand alternation {i}b");
        }
    }
}
