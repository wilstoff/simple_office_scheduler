using Microsoft.Playwright;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Covers the parts of the side panel that can be asserted reliably: the recurring checkbox and the
/// workshop event type both bind and reveal their dependent controls. EventFormPanel only builds a
/// RecurrencePattern when _model.IsRecurring is true, and RecurrenceExpander returns exactly one
/// occurrence when Recurrence is null, so a checkbox that failed to bind would produce a
/// single-occurrence event and a non-recurring Graph meeting with no error surfaced anywhere.
///
/// The full create-and-submit flow is deliberately not driven here. Every interaction round-trips to
/// the server and re-renders the panel, so Playwright outruns the circuit and fields set earlier are
/// reset by the render diff, failing for reasons unrelated to the app. Service-level coverage of the
/// same path is in RecurrenceExpanderTests and WorkshopTests.
/// </summary>
public class RecurringWorkshopUITests : IClassFixture<PlaywrightWebAppFixture>, IAsyncLifetime
{
    private readonly PlaywrightWebAppFixture _fixture;
    private IPage _page = null!;
    private IBrowserContext _context = null!;

    public RecurringWorkshopUITests(PlaywrightWebAppFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        _context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
            StorageState = _fixture.AuthState
        });
        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    /// <summary>
    /// The side panel opens from a calendar time-slot drag rather than a button, so this reaches the
    /// component the same way a person does.
    /// </summary>
    private async Task OpenNewEventPanelAsync()
    {
        await _page.GotoAsync($"{_fixture.BaseUrl}/calendar");
        await _page.WaitForSelectorAsync(".fc-timegrid-slot", new PageWaitForSelectorOptions { Timeout = 30000 });

        var slot = _page.Locator(".fc-timegrid-slot-lane").Nth(20);
        var box = await slot.BoundingBoxAsync()
            ?? throw new InvalidOperationException("no calendar slot to drag");

        await _page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + 2);
        await _page.Mouse.DownAsync();
        await _page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height * 3);
        await _page.Mouse.UpAsync();

        await _page.WaitForSelectorAsync("text=Event Type", new PageWaitForSelectorOptions { Timeout = 30000 });
    }

    [Fact]
    public async Task CheckingRecurringEvent_RevealsTheFrequencyControls()
    {
        await OpenNewEventPanelAsync();

        Assert.False(await _page.IsVisibleAsync("text=Frequency"));

        await _page.CheckAsync("#panelIsRecurring");

        await _page.WaitForSelectorAsync("text=Frequency", new PageWaitForSelectorOptions { Timeout = 10000 });
        Assert.True(await _page.IsVisibleAsync("text=Frequency"));
    }

    [Fact]
    public async Task WorkshopSelection_RevealsTheOwningTeamPicker()
    {
        await OpenNewEventPanelAsync();

        Assert.False(await _page.IsVisibleAsync("text=Owning team"));

        await _page.SelectOptionAsync("select.form-select >> nth=0", "Workshop");

        await _page.WaitForSelectorAsync("text=Owning team", new PageWaitForSelectorOptions { Timeout = 10000 });
        Assert.True(await _page.IsVisibleAsync("text=Owning team"));
    }
}
