using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Playwright;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;
using SimpleOfficeScheduler.Auth;
using SimpleOfficeScheduler.Components;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Auth;
using SimpleOfficeScheduler.Services.Calendar;
using SimpleOfficeScheduler.Services.Events;
using SimpleOfficeScheduler.Services;
using SimpleOfficeScheduler.Services.Recurrence;

namespace SimpleOfficeScheduler.Tests;

public class PlaywrightWebAppFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private IBrowser? _browser;
    private IPlaywright? _playwright;

    public string BaseUrl { get; private set; } = string.Empty;
    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized");
    public string AuthState { get; private set; } = string.Empty;

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"scheduler_uitest_{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        var projectDir = FindWebProjectDirectory();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = new[] { "--urls", "http://127.0.0.1:0" },
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            ContentRootPath = projectDir,
            EnvironmentName = "Development",
            WebRootPath = Path.Combine(projectDir, "wwwroot")
        });


        // Configuration options (defaults are fine for testing)
        builder.Services.Configure<ActiveDirectorySettings>(_ => { });
        builder.Services.Configure<GraphApiSettings>(_ => { });
        builder.Services.Configure<SeedUserSettings>(_ => { });
        builder.Services.Configure<RecurrenceSettings>(_ => { });
        builder.Services.Configure<TimezoneSettings>(_ => { });

        // NodaTime
        builder.Services.AddSingleton<NodaTime.IClock>(SystemClock.Instance);

        // Database (test SQLite)
        builder.Services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite($"Data Source={_dbPath}"));

        // Auth
        builder.Services.AddScoped<IAuthenticationService, LocalAuthService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddAuthentication("Cookies")
            .AddCookie("Cookies", options =>
            {
                options.LoginPath = "/login";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            });
        builder.Services.AddAuthorization();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthStateProvider>();

        // Real-time notifications
        builder.Services.AddSingleton<CalendarUpdateNotifier>();

        // Application services
        builder.Services.AddScoped<ICalendarInviteService, NoOpCalendarService>();
        builder.Services.AddScoped<RecurrenceExpander>();
        builder.Services.AddScoped<IEventService, EventService>();
        builder.Services.AddScoped<DbSeeder>();
        // No RecurrenceExpansionBackgroundService for testing

        // Blazor + Controllers (AddApplicationPart needed since entry assembly is test project)
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Program).Assembly)
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
            });

        _app = builder.Build();

        _app.UseStaticFiles();

        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.UseAntiforgery();
        _app.MapControllers();
        _app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode();

        await _app.StartAsync();
        BaseUrl = _app.Urls.First().TrimEnd('/');

        // Seed DB
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();

        if (!await db.Users.AnyAsync(u => u.Username == "testadmin"))
        {
            db.Users.Add(new AppUser
            {
                Username = "testadmin",
                DisplayName = "Test Admin",
                Email = "testadmin@test.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test123!"),
                IsLocalAccount = true,
                CreatedAt = SystemClock.Instance.GetCurrentInstant()
            });
            await db.SaveChangesAsync();
        }

        if (!await db.Users.AnyAsync(u => u.Username == "testuser2"))
        {
            db.Users.Add(new AppUser
            {
                Username = "testuser2",
                DisplayName = "Test User Two",
                Email = "testuser2@test.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Test123!"),
                IsLocalAccount = true,
                CreatedAt = SystemClock.Instance.GetCurrentInstant()
            });
            await db.SaveChangesAsync();
        }

        // Initialize Playwright
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        // Capture authenticated state for reuse across all tests
        var authContext = await _browser.NewContextAsync();
        var authPage = await authContext.NewPageAsync();
        await authPage.GotoAsync($"{BaseUrl}/login");
        await authPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await authPage.Locator("input[id='username'], input[name='username']").FillAsync("testadmin");
        await authPage.Locator("input[id='password'], input[name='password']").FillAsync("Test123!");
        await authPage.Locator("button[type='submit']").ClickAsync();
        await authPage.WaitForLoadStateAsync(LoadState.NetworkIdle);
        AuthState = await authContext.StorageStateAsync();
        await authContext.DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_browser != null) await _browser.DisposeAsync();
        _playwright?.Dispose();

        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    private static string FindWebProjectDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var webProjectDir = Path.Combine(dir, "src", "SimpleOfficeScheduler");
            if (Directory.Exists(webProjectDir) &&
                File.Exists(Path.Combine(webProjectDir, "SimpleOfficeScheduler.csproj")))
            {
                return webProjectDir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not find web project directory. Ensure the test runs from within the solution tree.");
    }
}
