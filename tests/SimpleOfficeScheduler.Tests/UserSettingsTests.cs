using System.Net;
using System.Net.Http.Json;
using SimpleOfficeScheduler.Models;
using Xunit;

namespace SimpleOfficeScheduler.Tests;

public class UserSettingsTests : IntegrationTestBase
{
    [Fact]
    public async Task Theme_Preference_Persists_In_DB()
    {
        await LoginAsync();

        var response = await Client.PutAsJsonAsync("/api/user/settings/theme",
            new { theme = "light" });
        response.EnsureSuccessStatusCode();

        var settings = await Client.GetFromJsonAsync<UserSettingsResponse>("/api/user/settings");
        Assert.NotNull(settings);
        Assert.Equal("light", settings.ThemePreference);
    }

    [Fact]
    public async Task Timezone_Preference_Persists_In_DB()
    {
        await LoginAsync();

        var response = await Client.PutAsJsonAsync("/api/user/settings/timezone",
            new { timeZoneId = "America/New_York" });
        response.EnsureSuccessStatusCode();

        var settings = await Client.GetFromJsonAsync<UserSettingsResponse>("/api/user/settings");
        Assert.NotNull(settings);
        Assert.Equal("America/New_York", settings.TimeZonePreference);
    }

    [Fact]
    public async Task Invalid_Theme_Returns_BadRequest()
    {
        await LoginAsync();

        var response = await Client.PutAsJsonAsync("/api/user/settings/theme",
            new { theme = "purple" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_Timezone_Returns_BadRequest()
    {
        await LoginAsync();

        var response = await Client.PutAsJsonAsync("/api/user/settings/timezone",
            new { timeZoneId = "Not/A/Real/Zone" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_Settings_Returns_User_Info()
    {
        await LoginAsync();

        var settings = await Client.GetFromJsonAsync<UserSettingsResponse>("/api/user/settings");
        Assert.NotNull(settings);
        Assert.Equal("dark", settings.ThemePreference);
        Assert.Null(settings.TimeZonePreference);
        Assert.Equal("Test Admin", settings.DisplayName);
        Assert.True(settings.IsLocalAccount);
    }

    [Fact]
    public async Task Login_Response_Includes_Theme_Preference()
    {
        // Set theme preference first
        await LoginAsync();
        await Client.PutAsJsonAsync("/api/user/settings/theme", new { theme = "light" });

        // Login again
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestUsername,
            password = TestPassword
        });
        response.EnsureSuccessStatusCode();

        var user = await response.Content.ReadFromJsonAsync<UserResponse>();
        Assert.NotNull(user);
        Assert.Equal("light", user.ThemePreference);
    }
}
