using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Tests that app static assets are served correctly through the real Program.cs pipeline.
/// Uses Testing environment (NOT Development) to avoid UseStaticWebAssets() masking issues.
///
/// Note: _framework/blazor.web.js CANNOT be tested via WebApplicationFactory because framework
/// assets are only discoverable in Development mode (via UseStaticWebAssets) or from published
/// output. In .NET 10, MapStaticAssets() serves blazor.web.js in Docker/Production through an
/// internal framework mechanism. The CI Docker smoke test (ci.yml) validates this end-to-end.
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
                builder.UseEnvironment("Testing");
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
