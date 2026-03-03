using Novell.Directory.Ldap;

namespace SimpleOfficeScheduler.Services.Ldap;

public class LdapConnectionFactory : ILdapConnectionFactory
{
    public ILdapConnection Create() => new LdapConnectionWrapper();
}

public class LdapConnectionWrapper : ILdapConnection
{
    private readonly LdapConnection _connection = new();

    public bool SecureSocketLayer
    {
        get => _connection.SecureSocketLayer;
        set => _connection.SecureSocketLayer = value;
    }

    public Task ConnectAsync(string host, int port) =>
        _connection.ConnectAsync(host, port);

    public Task BindAsync(string dn, string password) =>
        _connection.BindAsync(dn, password);

    public async IAsyncEnumerable<ILdapEntry> SearchAsync(
        string searchBase, int scope, string filter, string[] attributes, bool typesOnly)
    {
        var results = await _connection.SearchAsync(searchBase, scope, filter, attributes, typesOnly);
        await foreach (var entry in results)
        {
            yield return new LdapEntryWrapper(entry);
        }
    }

    public void Dispose() => _connection.Dispose();
}

public class LdapEntryWrapper : ILdapEntry
{
    private readonly LdapEntry _entry;

    public LdapEntryWrapper(LdapEntry entry) => _entry = entry;

    public LdapAttributeSet GetAttributeSet() => _entry.GetAttributeSet();
}
