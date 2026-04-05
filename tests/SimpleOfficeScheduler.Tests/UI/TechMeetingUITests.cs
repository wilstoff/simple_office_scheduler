using System.Text.Json;
using Microsoft.Playwright;

namespace SimpleOfficeScheduler.Tests;

public class TechMeetingUITests : IClassFixture<PlaywrightWebAppFixture>, IAsyncLifetime
{
    private readonly PlaywrightWebAppFixture _fixture;
    private IPage _page = null!;
    private IBrowserContext _context = null!;

    public TechMeetingUITests(PlaywrightWebAppFixture fixture)
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

    /// <summary>
    /// Creates a tech meeting event via the API and returns (eventId, occurrenceId).
    /// </summary>
    private async Task<(int EventId, int OccurrenceId)> CreateTechMeetingViaApi(string title = "Tech Meeting", bool useToday = false, int hour = 14)
    {
        if (_page.Url == "about:blank")
            await _page.GotoAsync($"{_fixture.BaseUrl}/");

        var startTime = useToday
            ? DateTime.Now.Date.AddHours(hour)
            : DateTime.Now.Date.AddDays(1).AddHours(hour);
        var endTime = startTime.AddHours(1);

        var result = await _page.EvaluateAsync<JsonElement>(@$"
            (async () => {{
                const response = await fetch('/api/events', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{
                        title: '{title}',
                        startTime: '{startTime:yyyy-MM-ddTHH:mm:ss}',
                        endTime: '{endTime:yyyy-MM-ddTHH:mm:ss}',
                        capacity: 5,
                        eventType: 1
                    }})
                }});
                return await response.json();
            }})()
        ");

        var eventId = result.GetProperty("id").GetInt32();
        var occurrenceId = result.GetProperty("occurrences")[0].GetProperty("id").GetInt32();
        return (eventId, occurrenceId);
    }

    private async Task<bool> WaitForEventOnCalendar(string text, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var texts = await _page.Locator(".fc-event").AllInnerTextsAsync();
            if (texts.Any(t => t.Contains(text)))
                return true;
            await _page.WaitForTimeoutAsync(500);
        }
        return false;
    }

    /// <summary>
    /// Opens the sidebar panel by clicking a calendar event with the given title.
    /// </summary>
    private async Task OpenSidebarForEvent(string title)
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });

        Assert.True(await WaitForEventOnCalendar(title), $"Event '{title}' should appear on calendar");

        var eventEl = _page.Locator($".fc-event:has-text('{title}')").First;
        await eventEl.ClickAsync();

        await _page.WaitForSelectorAsync(".side-panel.show", new() { Timeout = 5000 });
    }

    /// <summary>
    /// Assigns a contributor to an occurrence via API. Returns the contributor's user ID.
    /// </summary>
    private async Task<int> AssignContributorViaApi(int occurrenceId, string username)
    {
        var userId = await _page.EvaluateAsync<int>(@$"
            (async () => {{
                const r = await fetch('/api/users/search?q={username}');
                const users = await r.json();
                const user = users.find(u => u.username === '{username}');
                if (!user) throw new Error('User not found: {username}');
                await fetch('/api/events/occurrences/{occurrenceId}/contributors', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ userIds: [user.id] }})
                }});
                return user.id;
            }})()
        ");
        return userId;
    }

    /// <summary>
    /// Toggles lightning talks on an occurrence via API.
    /// </summary>
    private async Task ToggleLightningTalksViaApi(int occurrenceId, bool enable)
    {
        await _page.EvaluateAsync(@$"
            (async () => {{
                await fetch('/api/events/occurrences/{occurrenceId}/lightning-talks', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ isLightningTalks: {(enable ? "true" : "false")} }})
                }});
            }})()
        ");
    }

    // ── Sidebar Tech Meeting Tests ──────────────────────────────────

    [Fact]
    public async Task Sidebar_TechMeeting_ShowsLightningTalksToggle()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (_, occurrenceId) = await CreateTechMeetingViaApi("SB Toggle TM", useToday: true, hour: 7);
        await OpenSidebarForEvent("SB Toggle TM");

        // Owner should see a lightning talks toggle in the sidebar
        var toggle = _page.Locator(".side-panel [data-testid='sidebar-lightning-toggle']");
        await toggle.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await toggle.IsVisibleAsync());
    }

    [Fact]
    public async Task Sidebar_TechMeeting_ShowsContributors()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (_, occurrenceId) = await CreateTechMeetingViaApi("SB Contrib TM", useToday: true, hour: 8);
        await AssignContributorViaApi(occurrenceId, "testuser2");
        await OpenSidebarForEvent("SB Contrib TM");

        // Should show contributor name in the sidebar
        var panelContent = await _page.Locator(".side-panel.show").InnerTextAsync();
        Assert.Contains("Test User Two", panelContent);
    }

    [Fact]
    public async Task Sidebar_TechMeeting_NoSignupFormForRegular()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await CreateTechMeetingViaApi("SB NoSignup TM", useToday: true, hour: 16);
        await OpenSidebarForEvent("SB NoSignup TM");

        // Wait for panel to fully render
        await _page.WaitForTimeoutAsync(1000);

        // Signup input should NOT be visible for regular tech meetings
        var signupInput = _page.Locator(".side-panel input[placeholder*='Topic']");
        Assert.Equal(0, await signupInput.CountAsync());
    }

    [Fact]
    public async Task Sidebar_TechMeeting_ShowsSignupForLightningTalks()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (_, occurrenceId) = await CreateTechMeetingViaApi("SB LT Signup TM", useToday: true, hour: 11);
        await ToggleLightningTalksViaApi(occurrenceId, true);
        await OpenSidebarForEvent("SB LT Signup TM");

        // Signup input SHOULD be visible for lightning talks
        var signupInput = _page.Locator(".side-panel input[placeholder*='Topic']");
        await signupInput.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await signupInput.IsVisibleAsync());
    }

    [Fact]
    public async Task Sidebar_TechMeeting_ShowsTopicNotSignupCount()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, occurrenceId) = await CreateTechMeetingViaApi("SB Topic TM", useToday: true, hour: 12);

        // Set a topic via API
        await _page.EvaluateAsync(@$"
            (async () => {{
                await fetch('/api/events/occurrences/{occurrenceId}/name', {{
                    method: 'PATCH',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ nameSuffix: 'API Design Review' }})
                }});
            }})()
        ");

        await OpenSidebarForEvent("SB Topic TM");

        var panelContent = await _page.Locator(".side-panel.show").InnerTextAsync();
        // Should show the topic, not "0/5 signed up"
        Assert.Contains("API Design Review", panelContent);
        Assert.DoesNotContain("0/5 signed up", panelContent);
    }

    [Fact]
    public async Task Sidebar_TechMeeting_NameEditing_OwnerCanSetSuffix()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (_, occurrenceId) = await CreateTechMeetingViaApi("SB Name TM", useToday: true, hour: 13);
        await OpenSidebarForEvent("SB Name TM");

        // Owner should see a topic/suffix input
        var suffixInput = _page.Locator(".side-panel [data-testid='sidebar-name-suffix']");
        await suffixInput.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await suffixInput.IsVisibleAsync());
    }

    [Fact]
    public async Task Sidebar_TechMeeting_ContributorAssignment()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await CreateTechMeetingViaApi("SB Assign TM", useToday: true, hour: 15);
        await OpenSidebarForEvent("SB Assign TM");

        // Owner should see a contributor add UI
        var addContribInput = _page.Locator(".side-panel [data-testid='sidebar-add-contributor']");
        await addContribInput.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await addContribInput.IsVisibleAsync());
    }

    // ── Existing calendar/detail page tests ─────────────────────────

    [Fact]
    public async Task TechMeeting_ShowsGreenOnCalendar()
    {
        // Navigate to calendar first
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync(".fc-view", new() { Timeout = 10000 });

        // Create event using today so it appears on current week view
        await CreateTechMeetingViaApi("GreenCheck TM", useToday: true);

        // Wait for it to appear
        Assert.True(await WaitForEventOnCalendar("GreenCheck TM"),
            "Tech meeting should appear on calendar");

        // Find the event and verify green background
        var eventEl = _page.Locator(".fc-event:has-text('GreenCheck TM')").First;
        var bgColor = await eventEl.EvaluateAsync<string>(
            "el => getComputedStyle(el).backgroundColor");

        // #198754 = rgb(25, 135, 84)
        Assert.Contains("25", bgColor);
        Assert.Contains("135", bgColor);
        Assert.Contains("84", bgColor);
    }

    [Fact]
    public async Task TechMeeting_DetailPage_ShowsEventType()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, _) = await CreateTechMeetingViaApi();

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Verify "Tech Meeting" type indicator is shown
        var badge = _page.Locator("[data-testid='event-type-badge']");
        await badge.WaitForAsync(new() { Timeout = 10000 });
        var text = await badge.InnerTextAsync();
        Assert.Contains("Tech Meeting", text);
    }

    [Fact]
    public async Task TechMeeting_DetailPage_ShowsLightningTalksToggle()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, _) = await CreateTechMeetingViaApi();

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Wait for Blazor interactive rendering to complete
        await _page.WaitForSelectorAsync("[data-testid='event-type-badge']", new() { Timeout = 10000 });

        var lightningToggle = _page.Locator("[data-testid='lightning-talks-toggle']");
        await lightningToggle.WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await lightningToggle.IsVisibleAsync());
    }

    [Fact]
    public async Task TechMeeting_LightningTalks_ShowsSignupForm()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, occurrenceId) = await CreateTechMeetingViaApi();

        // Toggle lightning talks via API
        await _page.EvaluateAsync(@$"
            (async () => {{
                await fetch('/api/events/occurrences/{occurrenceId}/lightning-talks', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ isLightningTalks: true }})
                }});
            }})()
        ");

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("[data-testid='event-type-badge']", new() { Timeout = 10000 });

        // In lightning talks mode, the signup input should be visible
        var signupInput = _page.Locator("[data-testid='signup-message']");
        await signupInput.WaitForAsync(new() { Timeout = 10000 });
        Assert.True(await signupInput.IsVisibleAsync());
    }

    [Fact]
    public async Task TechMeeting_LightningTalksSignup_Works()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, occurrenceId) = await CreateTechMeetingViaApi();

        // Toggle lightning talks via API
        await _page.EvaluateAsync(@$"
            (async () => {{
                await fetch('/api/events/occurrences/{occurrenceId}/lightning-talks', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ isLightningTalks: true }})
                }});
            }})()
        ");

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("[data-testid='event-type-badge']", new() { Timeout = 10000 });

        // Fill in signup message
        var signupInput = _page.Locator("[data-testid='signup-message']");
        await signupInput.WaitForAsync(new() { Timeout = 10000 });
        await signupInput.FillAsync("My Lightning Talk Topic");

        // Click sign up button
        var signupBtn = _page.Locator("[data-testid='signup-btn']");
        await signupBtn.ClickAsync();

        // Wait for the Blazor server-side update
        await _page.WaitForTimeoutAsync(2000);

        // Verify signup appears in the content
        var content = await _page.ContentAsync();
        Assert.Contains("Test Admin", content);
    }

    // ── Past Occurrences Tests ──────────────────────────────────────

    /// <summary>
    /// Creates a tech meeting with a past occurrence and returns (eventId, occurrenceId).
    /// </summary>
    private async Task<(int EventId, int OccurrenceId)> CreatePastTechMeetingViaApi(string title = "Past TM")
    {
        if (_page.Url == "about:blank")
            await _page.GotoAsync($"{_fixture.BaseUrl}/");

        var startTime = DateTime.Now.Date.AddDays(-7).AddHours(14);
        var endTime = startTime.AddHours(1);

        var result = await _page.EvaluateAsync<JsonElement>(@$"
            (async () => {{
                const response = await fetch('/api/events', {{
                    method: 'POST',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{
                        title: '{title}',
                        startTime: '{startTime:yyyy-MM-ddTHH:mm:ss}',
                        endTime: '{endTime:yyyy-MM-ddTHH:mm:ss}',
                        capacity: 5,
                        eventType: 1
                    }})
                }});
                return await response.json();
            }})()
        ");

        var eventId = result.GetProperty("id").GetInt32();
        var occurrenceId = result.GetProperty("occurrences")[0].GetProperty("id").GetInt32();
        return (eventId, occurrenceId);
    }

    [Fact]
    public async Task TechMeeting_PastOccurrences_CollapsedByDefault()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, _) = await CreatePastTechMeetingViaApi("PastCollapsed TM");

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("[data-testid='event-type-badge']", new() { Timeout = 10000 });

        // Past occurrences header should exist
        var header = _page.Locator("[data-testid='past-occurrences-toggle']");
        await header.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await header.IsVisibleAsync());

        // The past occurrences table should be collapsed (not visible)
        var pastTable = _page.Locator("[data-testid='past-occurrences-table']");
        Assert.False(await pastTable.IsVisibleAsync());
    }

    [Fact]
    public async Task TechMeeting_PastOccurrences_ExpandShowsTable()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, _) = await CreatePastTechMeetingViaApi("PastExpand TM");

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("[data-testid='event-type-badge']", new() { Timeout = 10000 });

        // Click to expand
        var header = _page.Locator("[data-testid='past-occurrences-toggle']");
        await header.ClickAsync();

        // Table should now be visible
        var pastTable = _page.Locator("[data-testid='past-occurrences-table']");
        await pastTable.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await pastTable.IsVisibleAsync());
    }

    [Fact]
    public async Task TechMeeting_PastOccurrences_ReminderCheckboxToggleable()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/");
        var (eventId, occurrenceId) = await CreatePastTechMeetingViaApi("PastReminder TM");

        // Add a reminder definition via API
        await _page.EvaluateAsync(@$"
            (async () => {{
                await fetch('/api/events/{eventId}/reminders', {{
                    method: 'PUT',
                    headers: {{ 'Content-Type': 'application/json' }},
                    body: JSON.stringify({{ names: ['Recording Extension'] }})
                }});
            }})()
        ");

        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("[data-testid='event-type-badge']", new() { Timeout = 10000 });

        // Expand past occurrences
        var header = _page.Locator("[data-testid='past-occurrences-toggle']");
        await header.ClickAsync();

        var pastTable = _page.Locator("[data-testid='past-occurrences-table']");
        await pastTable.WaitForAsync(new() { Timeout = 5000 });

        // Find the reminder checkbox in the past table and verify it's clickable
        var checkbox = pastTable.Locator("input[type='checkbox']").First;
        await checkbox.WaitForAsync(new() { Timeout = 5000 });
        Assert.False(await checkbox.IsCheckedAsync());

        await checkbox.ClickAsync();
        await _page.WaitForTimeoutAsync(1000);

        // Reload to verify persistence
        await _page.GotoAsync($"{_fixture.BaseUrl}/events/{eventId}");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await _page.WaitForSelectorAsync("[data-testid='past-occurrences-toggle']", new() { Timeout = 10000 });
        await _page.Locator("[data-testid='past-occurrences-toggle']").ClickAsync();

        var reloadedCheckbox = _page.Locator("[data-testid='past-occurrences-table'] input[type='checkbox']").First;
        await reloadedCheckbox.WaitForAsync(new() { Timeout = 5000 });
        Assert.True(await reloadedCheckbox.IsCheckedAsync());
    }
}
