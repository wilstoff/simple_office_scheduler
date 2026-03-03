using Novell.Directory.Ldap;

namespace SimpleOfficeScheduler.Services.Ldap;

public interface ILdapConnectionFactory
{
    ILdapConnection Create();
}

public interface ILdapConnection : IDisposable
{
    bool SecureSocketLayer { get; set; }
    Task ConnectAsync(string host, int port);
    Task BindAsync(string dn, string password);
    IAsyncEnumerable<ILdapEntry> SearchAsync(string searchBase, int scope, string filter, string[] attributes, bool typesOnly);
}

public interface ILdapEntry
{
    LdapAttributeSet GetAttributeSet();
}
