using SimpleOfficeScheduler.Services.Auth;

namespace SimpleOfficeScheduler.Tests.Component;

public class UsernameNormalizerTests
{
    [Theory]
    [InlineData("john", "DC=abc,DC=com", "john")]
    [InlineData("john@abc.com", "DC=abc,DC=com", "john")]
    [InlineData("john@gmail.com", "DC=abc,DC=com", "john@gmail.com")]
    [InlineData("john@abc.com", "OU=Users,OU=Chicago,DC=abc,DC=com", "john")]
    [InlineData("john@Abc.COM", "dc=abc,dc=com", "john")]
    [InlineData("john@abc.com", "", "john@abc.com")]
    [InlineData("john@abc.com", "OU=Users", "john@abc.com")]
    [InlineData("alice@corp.example.com", "DC=corp,DC=example,DC=com", "alice")]
    [InlineData("alice@example.com", "DC=corp,DC=example,DC=com", "alice@example.com")]
    [InlineData(null, "DC=abc,DC=com", null)]
    [InlineData("", "DC=abc,DC=com", "")]
    [InlineData("john@abc.com", "DC = abc , DC = com", "john")]
    public void NormalizeForLdapBind_ReturnsExpected(string? username, string searchBase, string? expected)
    {
        var result = UsernameNormalizer.NormalizeForLdapBind(username!, searchBase);
        Assert.Equal(expected, result);
    }
}
