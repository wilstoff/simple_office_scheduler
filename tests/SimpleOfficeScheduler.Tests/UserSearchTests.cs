using System.Net;
using System.Net.Http.Json;

namespace SimpleOfficeScheduler.Tests;

public class UserSearchTests : IntegrationTestBase
{
    [Fact]
    public async Task SearchUsers_ByDisplayName_ReturnsMatch()
    {
        await LoginAsync();
        await CreateSecondUserAsync("searchuser1");

        var response = await Client.GetAsync("/api/users/search?q=User search");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Contains(results, r => r.Username == "searchuser1");
    }

    [Fact]
    public async Task SearchUsers_ByUsername_ReturnsMatch()
    {
        await LoginAsync();
        await CreateSecondUserAsync("findme");

        var response = await Client.GetAsync("/api/users/search?q=findme");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Contains(results, r => r.Username == "findme");
    }

    [Fact]
    public async Task SearchUsers_TooShortQuery_ReturnsEmpty()
    {
        await LoginAsync();

        var response = await Client.GetAsync("/api/users/search?q=a");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsers_ExcludesSpecifiedUser()
    {
        await LoginAsync();

        // testadmin should match "test" but be excluded
        var response = await Client.GetAsync("/api/users/search?q=test&excludeUserId=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.DoesNotContain(results, r => r.Username == "testadmin");
    }

    [Fact]
    public async Task SearchUsers_Unauthenticated_ReturnsUnauthorized()
    {
        var response = await Client.GetAsync("/api/users/search?q=test");
        // Cookie auth redirects to login page with 302
        Assert.True(
            response.StatusCode == HttpStatusCode.Unauthorized ||
            response.StatusCode == HttpStatusCode.Redirect,
            $"Expected Unauthorized or Redirect but got {response.StatusCode}");
    }

    [Fact]
    public async Task SearchUsers_NoMatch_ReturnsEmpty()
    {
        await LoginAsync();

        var response = await Client.GetAsync("/api/users/search?q=zzzznonexistent");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Empty(results);
    }

    [Fact]
    public async Task TransferOwnership_ViaSearch_WorksEndToEnd()
    {
        await LoginAsync();
        var evt = await CreateEventAsync("Transfer Search Test");
        await CreateSecondUserAsync("transfertarget");

        // Search for the user
        var searchResponse = await Client.GetAsync("/api/users/search?q=transfertarget");
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);

        var results = await searchResponse.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Single(results);

        // Transfer ownership using the found user ID
        var transferResponse = await Client.PostAsync(
            $"/api/events/{evt.Id}/transfer?newOwnerId={results[0].Id}", null);
        Assert.Equal(HttpStatusCode.OK, transferResponse.StatusCode);
    }

    // DTO matching the API response shape
    private class UserSearchResultDto
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsAdOnly { get; set; }
    }
}
