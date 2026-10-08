using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Novell.Directory.Ldap;
using SimpleOfficeScheduler.Data;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Auth;
using SimpleOfficeScheduler.Services.Ldap;
using ILdapConnection = SimpleOfficeScheduler.Services.Ldap.ILdapConnection;
using ILdapEntry = SimpleOfficeScheduler.Services.Ldap.ILdapEntry;

namespace SimpleOfficeScheduler.Tests;

/// <summary>
/// An LDAP simple bind with a name and an empty password is an "unauthenticated bind" (RFC 4513
/// section 5.1.2). Active Directory reports it as a success without checking any credential, so
/// passing the password straight through let anyone sign in as anyone by leaving it blank. The mock
/// connection here accepts every bind, the way AD does, so only the service's own check stands
/// between a blank password and a session.
/// </summary>
public class LdapAuthServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContextFactory _dbFactory;
    private readonly AppDbContext _db;
    private readonly Mock<ILdapConnection> _ldap;
    private readonly LdapAuthService _sut;

    public LdapAuthServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _dbFactory = new TestDbContextFactory(options);
        _db = _dbFactory.CreateDbContext();
        _db.Database.EnsureCreated();

        _ldap = new Mock<ILdapConnection>();
        _ldap.Setup(c => c.ConnectAsync(It.IsAny<string>(), It.IsAny<int>())).Returns(Task.CompletedTask);
        _ldap.Setup(c => c.BindAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        _ldap.Setup(c => c.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string[]>(), It.IsAny<bool>()))
            .Returns(NoEntries());

        var factory = new Mock<ILdapConnectionFactory>();
        factory.Setup(f => f.Create()).Returns(_ldap.Object);

        _sut = new LdapAuthService(
            _dbFactory,
            Options.Create(new ActiveDirectorySettings
            {
                Enabled = true,
                Domain = "TESTDOMAIN",
                Host = "ldap.test.local",
                SearchBase = "DC=test,DC=local"
            }),
            factory.Object,
            NullLogger<LdapAuthService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class TestDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestDbContextFactory(DbContextOptions<AppDbContext> options) => _options = options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    private static async IAsyncEnumerable<ILdapEntry> NoEntries()
    {
        await Task.CompletedTask;
        yield break;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task BlankPassword_IsRejected_WithoutBindingOrCreatingAUser(string? password)
    {
        var result = await _sut.ValidateAsync("victim", password!);

        Assert.False(result.Success);
        Assert.Null(result.User);
        _ldap.Verify(c => c.BindAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.False(await _db.Users.AnyAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task BlankPassword_IsRejected_ForALocalAccountToo(string? password)
    {
        _db.Users.Add(new AppUser
        {
            Username = "local",
            DisplayName = "Local",
            Email = "local@test.local",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Secret123!"),
            IsLocalAccount = true
        });
        await _db.SaveChangesAsync();

        var result = await _sut.ValidateAsync("local", password!);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task RealPassword_ThatBinds_SignsIn()
    {
        var result = await _sut.ValidateAsync("someone", "correct horse");

        Assert.True(result.Success);
        Assert.Equal("someone", result.User!.Username);
        _ldap.Verify(c => c.BindAsync("TESTDOMAIN\\someone", "correct horse"), Times.Once);
    }

    [Fact]
    public async Task RealPassword_ThatFailsToBind_IsRejected()
    {
        _ldap.Setup(c => c.BindAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new LdapException("Invalid Credentials", LdapException.InvalidCredentials, null));

        var result = await _sut.ValidateAsync("someone", "wrong");

        Assert.False(result.Success);
        Assert.False(await _db.Users.AnyAsync());
    }
}
