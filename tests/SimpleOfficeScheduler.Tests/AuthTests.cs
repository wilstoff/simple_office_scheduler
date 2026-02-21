using System.Net;
using System.Net.Http.Json;
using SimpleOfficeScheduler.Models;

namespace SimpleOfficeScheduler.Tests;

public class AuthTests : IntegrationTestBase
{
    [Fact]
    public async Task Login_ValidCredentials_ReturnsOkWithUserInfo()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestUsername,
            password = TestPassword
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var user = await response.Content.ReadFromJsonAsync<UserResponse>();
        Assert.NotNull(user);
        Assert.Equal(TestUsername, user.Username);
        Assert.Equal("Test Admin", user.DisplayName);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestUsername,
            password = "WrongPassword"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_NonExistentUser_Returns401()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "nobody",
            password = "anything"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutLogin_ReturnsUnauthorized()
    {
        var response = await Client.GetAsync("/api/events/search");

        // Should redirect to login or return 401/302
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Redirect ||
            response.RequestMessage?.RequestUri?.AbsolutePath == "/login",
            $"Expected unauthorized/redirect but got {response.StatusCode}");
    }

    [Fact]
    public async Task Logout_ClearsSession()
    {
        await LoginAsync();

        // Verify logged in
        var checkResponse = await Client.GetAsync("/api/events/search");
        Assert.Equal(HttpStatusCode.OK, checkResponse.StatusCode);

        // Logout
        var logoutResponse = await Client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        // Verify logged out
        var afterLogout = await Client.GetAsync("/api/events/search");
        Assert.True(
            afterLogout.StatusCode == HttpStatusCode.Unauthorized ||
            afterLogout.StatusCode == HttpStatusCode.Redirect ||
            afterLogout.RequestMessage?.RequestUri?.AbsolutePath == "/login",
            $"Expected unauthorized after logout but got {afterLogout.StatusCode}");
    }
}
