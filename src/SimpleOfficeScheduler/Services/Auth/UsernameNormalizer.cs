namespace SimpleOfficeScheduler.Services.Auth;

public static class UsernameNormalizer
{
    public static string NormalizeForLdapBind(string username, string searchBase)
    {
        if (string.IsNullOrEmpty(username))
            return username;

        var atIndex = username.LastIndexOf('@');
        if (atIndex < 0)
            return username;

        var domain = username[(atIndex + 1)..];
        var searchBaseDomain = ExtractDomainFromSearchBase(searchBase);
        if (searchBaseDomain is null)
            return username;

        if (string.Equals(domain, searchBaseDomain, StringComparison.OrdinalIgnoreCase))
            return username[..atIndex];

        return username;
    }

    private static string? ExtractDomainFromSearchBase(string searchBase)
    {
        if (string.IsNullOrWhiteSpace(searchBase))
            return null;

        var dcParts = new List<string>();
        foreach (var rawPart in searchBase.Split(','))
        {
            var part = rawPart.Trim();
            var eq = part.IndexOf('=');
            if (eq < 0) continue;

            var key = part[..eq].Trim();
            if (!string.Equals(key, "DC", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = part[(eq + 1)..].Trim();
            if (value.Length > 0)
                dcParts.Add(value);
        }

        return dcParts.Count == 0 ? null : string.Join('.', dcParts);
    }
}
