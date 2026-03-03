using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NodaTime;
using Novell.Directory.Ldap;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Ldap;
using ILdapConnection = SimpleOfficeScheduler.Services.Ldap.ILdapConnection;
using ILdapEntry = SimpleOfficeScheduler.Services.Ldap.ILdapEntry;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// Integration tests for UserSearchService with AD enabled and a mock LDAP layer.
/// </summary>
public class UserSearchAdTests : IAsyncLifetime
{
    private readonly string _dbPath;
    private readonly Mock<ILdapConnectionFactory> _mockLdapFactory;
    private readonly Mock<ILdapConnection> _mockConnection;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public UserSearchAdTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"scheduler_test_{Guid.NewGuid():N}.db");

        _mockLdapFactory = new Mock<ILdapConnectionFactory>();
        _mockConnection = new Mock<ILdapConnection>();
        _mockLdapFactory.Setup(f => f.Create()).Returns(_mockConnection.Object);

        // Default: connection and bind succeed
        _mockConnection.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        _mockConnection.Setup(c => c.BindAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        // Default: empty search results
        _mockConnection.Setup(c => c.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(EmptyAsyncEnumerable());

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    // Use unique SQLite DB per test
                    var descriptor = services.SingleOrDefault(
                        d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                    if (descriptor != null) services.Remove(descriptor);
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseSqlite($"Data Source={_dbPath}",
                            o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

                    // Remove background service
                    var bgService = services.SingleOrDefault(
                        d => d.ImplementationType?.Name == "RecurrenceExpansionBackgroundService");
                    if (bgService != null) services.Remove(bgService);

                    // Enable AD settings via Options (doesn't affect auth service registration)
                    services.Configure<ActiveDirectorySettings>(o =>
                    {
                        o.Enabled = true;
                        o.Domain = "TESTDOMAIN";
                        o.Host = "ldap.test.local";
                        o.Port = 389;
                        o.SearchBase = "DC=test,DC=local";
                        o.ServiceAccountUsername = "svc_test";
                        o.ServiceAccountPassword = "testpass";
                    });

                    // Replace real LDAP factory with mock
                    var existing = services.FirstOrDefault(
                        d => d.ServiceType == typeof(ILdapConnectionFactory));
                    if (existing != null) services.Remove(existing);
                    services.AddSingleton<ILdapConnectionFactory>(_mockLdapFactory.Object);
                });
            });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
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
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        if (File.Exists(_dbPath))
            try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task SearchWithAd_MergesAdAndDbResults()
    {
        await SeedUserAsync("dbuser", "DB User");
        SetupAdSearchResults(("aduser1", "AD User One", "aduser1@testdomain.local"));
        await LoginAsync();

        var response = await _client.GetAsync("/api/users/search?q=user");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Contains(results, r => r.Username == "dbuser" && !r.IsAdOnly);
        Assert.Contains(results, r => r.Username == "aduser1" && r.IsAdOnly);
    }

    [Fact]
    public async Task SearchWithAd_DeduplicatesDbAndAd()
    {
        await SeedUserAsync("dupuser", "Dup User");
        SetupAdSearchResults(("dupuser", "Dup User AD", "dupuser@testdomain.local"));
        await LoginAsync();

        var response = await _client.GetAsync("/api/users/search?q=dupuser");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Single(results, r => r.Username == "dupuser");
        Assert.False(results.First(r => r.Username == "dupuser").IsAdOnly);
    }

    [Fact]
    public async Task SearchWithAd_BindsWithDomainBackslashUsername()
    {
        await LoginAsync();

        await _client.GetAsync("/api/users/search?q=anything");

        _mockConnection.Verify(
            c => c.BindAsync("TESTDOMAIN\\svc_test", "testpass"), Times.Once);
    }

    [Fact]
    public async Task SearchWithAd_LdapFailure_ReturnsDbResultsOnly()
    {
        await SeedUserAsync("safeuser", "Safe User");
        _mockConnection.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<int>()))
            .ThrowsAsync(new LdapException("Connection failed", 91, "test"));
        await LoginAsync();

        var response = await _client.GetAsync("/api/users/search?q=user");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.Contains(results, r => r.Username == "safeuser");
        Assert.DoesNotContain(results, r => r.IsAdOnly);
    }

    [Fact]
    public async Task SearchWithAd_RespectsMaxResults()
    {
        for (int i = 1; i <= 8; i++)
            await SeedUserAsync($"bulkuser{i}", $"Bulk User {i}");

        var adEntries = Enumerable.Range(1, 5).Select(i =>
        {
            var entry = new Mock<ILdapEntry>();
            entry.Setup(e => e.GetAttributeSet())
                .Returns(CreateAttributeSet($"adextra{i}", $"AD Extra {i}", $"adextra{i}@testdomain.local"));
            return entry.Object;
        }).ToArray();
        _mockConnection.Setup(c => c.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(ToAsyncEnumerable(adEntries));
        await LoginAsync();

        var response = await _client.GetAsync("/api/users/search?q=user");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<UserSearchResultDto>>();
        Assert.NotNull(results);
        Assert.True(results.Count <= 10, $"Expected at most 10 results but got {results.Count}");
    }

    [Fact]
    public async Task EnsureUser_FoundInAd_CreatesLocalRecord()
    {
        SetupAdSearchResults(("newaduser", "New AD User", "newaduser@testdomain.local"));
        await LoginAsync();

        var response = await _client.PostAsJsonAsync("/api/users/ensure",
            new { username = "newaduser" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<UserSearchResultDto>();
        Assert.NotNull(result);
        Assert.Equal("newaduser", result.Username);
        Assert.Equal("New AD User", result.DisplayName);

        // Verify user was persisted to DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbUser = await db.Users.FirstOrDefaultAsync(u => u.Username == "newaduser");
        Assert.NotNull(dbUser);
        Assert.False(dbUser.IsLocalAccount);
    }

    // --- Helpers ---

    private async Task LoginAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username = "testadmin",
            password = "Test123!"
        });
        response.EnsureSuccessStatusCode();
    }

    private async Task SeedUserAsync(string username, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync(u => u.Username == username))
        {
            db.Users.Add(new AppUser
            {
                Username = username,
                DisplayName = displayName,
                Email = $"{username}@test.local",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("Password1!"),
                IsLocalAccount = true,
                CreatedAt = SystemClock.Instance.GetCurrentInstant()
            });
            await db.SaveChangesAsync();
        }
    }

    private void SetupAdSearchResults(params (string Username, string DisplayName, string Email)[] users)
    {
        var entries = users.Select(u =>
        {
            var entry = new Mock<ILdapEntry>();
            entry.Setup(e => e.GetAttributeSet())
                .Returns(CreateAttributeSet(u.Username, u.DisplayName, u.Email));
            return entry.Object;
        }).ToArray();

        _mockConnection.Setup(c => c.SearchAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(ToAsyncEnumerable(entries));
    }

    private static LdapAttributeSet CreateAttributeSet(string username, string displayName, string email)
    {
        var attrs = new LdapAttributeSet();
        attrs.Add(new LdapAttribute("sAMAccountName", username));
        attrs.Add(new LdapAttribute("displayName", displayName));
        attrs.Add(new LdapAttribute("mail", email));
        return attrs;
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<ILdapEntry> EmptyAsyncEnumerable()
    {
        yield break;
    }
#pragma warning restore CS1998

    private static async IAsyncEnumerable<ILdapEntry> ToAsyncEnumerable(params ILdapEntry[] entries)
    {
        foreach (var entry in entries)
        {
            await Task.Yield();
            yield return entry;
        }
    }

    private class UserSearchResultDto
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsAdOnly { get; set; }
    }
}
