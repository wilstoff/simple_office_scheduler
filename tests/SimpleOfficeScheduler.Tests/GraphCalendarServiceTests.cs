using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void CalendarTargetEmail_Is_Owner_In_ApplicationMode()
    {
        var settings = Options.Create(new GraphApiSettings
        {
            TenantId = "t",
            ClientId = "c",
            ClientSecret = "s"
        });
        var service = new GraphCalendarService(settings, NullLogger<GraphCalendarService>.Instance);
        var owner = new AppUser { Email = "owner@test.com" };

        Assert.Equal("owner@test.com", service.GetCalendarTargetEmail(owner));
    }

    [Fact]
    public void CalendarTargetEmail_Is_ServiceAccount_In_DelegatedMode()
    {
        var settings = Options.Create(new GraphApiSettings
        {
            TenantId = "t",
            ClientId = "c",
            ServiceAccountEmail = "svc@test.com",
            ServiceAccountPassword = "pwd"
        });
        var service = new GraphCalendarService(settings, NullLogger<GraphCalendarService>.Instance);
        var owner = new AppUser { Email = "owner@test.com" };

        Assert.Equal("svc@test.com", service.GetCalendarTargetEmail(owner));
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

    [Fact]
    public void UseDelegatedAuth_True_When_ServiceAccount_Configured()
    {
        var settings = new GraphApiSettings
        {
            TenantId = "t",
            ClientId = "c",
            ServiceAccountEmail = "svc@test.com",
            ServiceAccountPassword = "pwd"
        };

        Assert.True(settings.UseDelegatedAuth);
    }

    [Fact]
    public void UseDelegatedAuth_False_When_No_ServiceAccount()
    {
        var settings = new GraphApiSettings
        {
            TenantId = "t",
            ClientId = "c",
            ClientSecret = "s"
        };

        Assert.False(settings.UseDelegatedAuth);
    }

    [Fact]
    public void DI_Condition_Selects_Graph_When_ServiceAccount_Without_ClientSecret()
    {
        // This tests the exact condition used in Program.cs
        var settings = new GraphApiSettings
        {
            TenantId = "fake-tenant",
            ClientId = "fake-client",
            ServiceAccountEmail = "svc@test.com",
            ServiceAccountPassword = "password"
        };

        var shouldUseGraph = !string.IsNullOrEmpty(settings.ClientId) &&
                             !string.IsNullOrEmpty(settings.TenantId) &&
                             (!string.IsNullOrEmpty(settings.ClientSecret) || settings.UseDelegatedAuth);

        Assert.True(shouldUseGraph);
    }
}
