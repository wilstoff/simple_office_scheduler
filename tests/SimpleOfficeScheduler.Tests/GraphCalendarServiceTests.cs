using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Calendar;

namespace SimpleOfficeScheduler.Tests;

public class GraphCalendarServiceTests : IDisposable
{
    private readonly List<WebApplicationFactory<Program>> _factories = new();

    public void Dispose()
    {
        foreach (var f in _factories) f.Dispose();
    }

    private WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?> config)
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(config);
                });
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseSqlite("Data Source=:memory:"));

                    var bgService = services.SingleOrDefault(
                        d => d.ImplementationType?.Name == "RecurrenceExpansionBackgroundService");
                    if (bgService != null) services.Remove(bgService);
                });
            });
        _factories.Add(factory);
        return factory;
    }

    [Fact]
    public void DI_Registers_NoOp_When_TargetMailbox_Missing()
    {
        var config = new Dictionary<string, string?>
        {
            ["GraphApi:TenantId"] = "t",
            ["GraphApi:ClientId"] = "c",
            ["GraphApi:ClientSecret"] = "s",
            ["GraphApi:TargetMailbox"] = "",
        };
        var factory = CreateFactory(config);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICalendarInviteService>();

        Assert.IsType<NoOpCalendarService>(service);
    }

    [Fact]
    public void DI_Registers_NoOp_When_No_GraphApi_Settings()
    {
        var config = new Dictionary<string, string?>
        {
            ["GraphApi:TenantId"] = "",
            ["GraphApi:ClientId"] = "",
        };
        var factory = CreateFactory(config);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ICalendarInviteService>();

        Assert.IsType<NoOpCalendarService>(service);
    }
}
