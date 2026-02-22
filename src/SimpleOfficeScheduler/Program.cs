using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
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

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<ActiveDirectorySettings>(builder.Configuration.GetSection("ActiveDirectory"));
builder.Services.Configure<GraphApiSettings>(builder.Configuration.GetSection("GraphApi"));
builder.Services.Configure<SeedUserSettings>(builder.Configuration.GetSection("SeedUser"));
builder.Services.Configure<RecurrenceSettings>(builder.Configuration.GetSection("Recurrence"));
builder.Services.Configure<TimezoneSettings>(builder.Configuration.GetSection("Timezone"));

// NodaTime
builder.Services.AddSingleton<IClock>(SystemClock.Instance);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Authentication services
var adSettings = builder.Configuration.GetSection("ActiveDirectory").Get<ActiveDirectorySettings>();
if (adSettings?.Enabled == true)
{
    builder.Services.AddScoped<IAuthenticationService, LdapAuthService>();
}
else
{
    builder.Services.AddScoped<IAuthenticationService, LocalAuthService>();
}

// Blazor auth
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

// Calendar invite service
var graphSettings = builder.Configuration.GetSection("GraphApi").Get<GraphApiSettings>();
if (!string.IsNullOrEmpty(graphSettings?.ClientId) && !string.IsNullOrEmpty(graphSettings?.TenantId))
{
    builder.Services.AddScoped<ICalendarInviteService, GraphCalendarService>();
}
else
{
    builder.Services.AddScoped<ICalendarInviteService, NoOpCalendarService>();
}

// Real-time calendar update notifications
builder.Services.AddSingleton<CalendarUpdateNotifier>();

// Application services
builder.Services.AddScoped<RecurrenceExpander>();
builder.Services.AddScoped<IEventService, EventService>();
builder.Services.AddScoped<DbSeeder>();
builder.Services.AddHostedService<RecurrenceExpansionBackgroundService>();

// Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Controllers with NodaTime JSON serialization
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
    });

var app = builder.Build();

// Seed database
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DbSeeder>();
    await seeder.SeedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Make Program accessible for WebApplicationFactory<Program> in tests
public partial class Program { }
