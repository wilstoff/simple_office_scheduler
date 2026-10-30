using Novell.Directory.Ldap;
using SimpleOfficeScheduler.Models;
using SimpleOfficeScheduler.Services.Auth;

namespace SimpleOfficeScheduler.Services.Ldap;

/// <summary>
/// Reads a user's name and email from an AD entry. Sign-in, user search and ensure all copy AD users
/// into the local Users table through here, so they agree on what the email is.
/// </summary>
public static class AdUserAttributes
{
    public static readonly string[] Requested = { "displayName", "mail", "userPrincipalName", "sAMAccountName" };

    public static string? Get(LdapAttributeSet attrs, string name)
    {
        if (!attrs.ContainsKey(name))
            return null;
        var value = attrs[name].StringValue;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>mail, then userPrincipalName, then <see cref="FallbackEmail"/>.</summary>
    public static string ResolveEmail(LdapAttributeSet attrs, string username, ActiveDirectorySettings settings) =>
        Get(attrs, "mail") ?? Get(attrs, "userPrincipalName") ?? FallbackEmail(username, settings);

    /// <summary>
    /// A guess for a user AD gave no address for. It uses the DNS domain from SearchBase, not the
    /// NetBIOS Domain: "jdoe@corp" is not an address, and Exchange drops invites sent to it.
    /// </summary>
    public static string FallbackEmail(string username, ActiveDirectorySettings settings)
    {
        var domain = UsernameNormalizer.ExtractDomainFromSearchBase(settings.SearchBase) ?? settings.Domain;
        return $"{username}@{domain.ToLowerInvariant()}";
    }
}
