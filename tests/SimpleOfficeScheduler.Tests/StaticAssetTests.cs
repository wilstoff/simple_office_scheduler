using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Tests that critical static assets are served correctly through the REAL Program.cs pipeline.
/// Uses Development environment so that static web assets (including framework files from NuGet
/// packages like blazor.web.js) are discovered — matching the Playwright fixture and published
/// Docker behavior.
/// </summary>
public class StaticAssetTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
            });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task BlazorWebJs_Returns200()
    {
        var response = await _client.GetAsync("/_framework/blazor.web.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FullCalendarJs_Returns200()
    {
        var response = await _client.GetAsync("/js/fullcalendar-interop.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AppCss_Returns200()
    {
        var response = await _client.GetAsync("/app.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
