using Kevsoft.MigrateMongo.Config;

namespace Kevsoft.MigrateMongo.Tool.Tunnel;

/// <summary>Rewrites a MongoDB connection string to go through an SSH tunnel.</summary>
internal static class MongoUrlRewriter
{
    private const int DefaultMongoPort = 27017;

    /// <summary>Points the driver at a local SOCKS5 proxy (MongoDB.Driver 3.12+), keeping the hosts as they are.</summary>
    public static string ForSocks(string url, int proxyPort)
    {
        var parts = Parse(url);
        if (HasOption(parts.Query, "proxyHost"))
        {
            throw new ConfigException("'mongodb.url' already sets proxyHost; remove it or the tunnel.");
        }

        return parts with { Query = AddOptions(parts.Query, $"proxyHost=127.0.0.1&proxyPort={proxyPort}") };
    }

    /// <summary>
    /// Replaces the hosts with the local end of a forwarded port and forces a direct connection, as a forwarded port
    /// only reaches one server. Returns the server the tunnel has to forward to.
    /// </summary>
    public static (string Url, string Host, int Port) ForLocal(string url, int localPort)
    {
        var parts = Parse(url);
        if (parts.Scheme == "mongodb+srv")
        {
            throw new ConfigException("A 'local' tunnel can't be used with a mongodb+srv:// url as it only forwards to one server; use 'mode: socks' (MongoDB.Driver 3.12+) or list a host in a mongodb:// url.");
        }

        var (host, port) = SplitHost(parts.Hosts.Split(',')[0]);
        var query = RemoveOption(parts.Query, "directConnection");
        var rewritten = parts with { Hosts = $"127.0.0.1:{localPort}", Query = AddOptions(query, "directConnection=true") };
        return ((string)rewritten, host, port);
    }

    public static bool UsesTls(string url)
    {
        var parts = Parse(url);
        return parts.Scheme == "mongodb+srv" || IsTrue(parts.Query, "tls") || IsTrue(parts.Query, "ssl");
    }

    private static UrlParts Parse(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0 || url[..schemeEnd] is not ("mongodb" or "mongodb+srv"))
        {
            throw new ConfigException("'mongodb.url' must start with mongodb:// or mongodb+srv:// to use a tunnel.");
        }

        var rest = url[(schemeEnd + 3)..];
        var authorityEnd = rest.IndexOfAny(['/', '?']);
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        var remainder = authorityEnd < 0 ? "" : rest[authorityEnd..];

        var at = authority.LastIndexOf('@');
        var userInfo = at < 0 ? "" : authority[..(at + 1)];
        var hosts = authority[(at + 1)..];
        if (hosts.Length == 0)
        {
            throw new ConfigException("'mongodb.url' has no host.");
        }

        var queryStart = remainder.IndexOf('?');
        var path = queryStart < 0 ? remainder : remainder[..queryStart];
        var query = queryStart < 0 ? "" : remainder[(queryStart + 1)..];
        return new UrlParts(url[..schemeEnd], userInfo, hosts, path.TrimStart('/'), query);
    }

    private static (string Host, int Port) SplitHost(string hostAndPort)
    {
        string host;
        string? port;
        if (hostAndPort.StartsWith('['))
        {
            var close = hostAndPort.IndexOf(']');
            host = hostAndPort[1..close];
            port = close + 1 < hostAndPort.Length && hostAndPort[close + 1] == ':' ? hostAndPort[(close + 2)..] : null;
        }
        else
        {
            var colon = hostAndPort.LastIndexOf(':');
            host = colon < 0 ? hostAndPort : hostAndPort[..colon];
            port = colon < 0 ? null : hostAndPort[(colon + 1)..];
        }

        if (port is null)
        {
            return (host, DefaultMongoPort);
        }

        return int.TryParse(port, out var number) && number is > 0 and <= 65535
            ? (host, number)
            : throw new ConfigException($"'mongodb.url' has an invalid port in '{hostAndPort}'.");
    }

    private static IEnumerable<string> Options(string query) => query.Split('&', StringSplitOptions.RemoveEmptyEntries);

    private static string OptionName(string option) => option.Split('=', 2)[0];

    private static bool HasOption(string query, string name) =>
        Options(query).Any(o => string.Equals(OptionName(o), name, StringComparison.OrdinalIgnoreCase));

    private static bool IsTrue(string query, string name) =>
        Options(query).Any(o => string.Equals(o, $"{name}=true", StringComparison.OrdinalIgnoreCase));

    private static string RemoveOption(string query, string name) =>
        string.Join('&', Options(query).Where(o => !string.Equals(OptionName(o), name, StringComparison.OrdinalIgnoreCase)));

    private static string AddOptions(string query, string options) => query.Length == 0 ? options : $"{query}&{options}";

    private sealed record UrlParts(string Scheme, string UserInfo, string Hosts, string Path, string Query)
    {
        public static implicit operator string(UrlParts parts) =>
            $"{parts.Scheme}://{parts.UserInfo}{parts.Hosts}/{parts.Path}{(parts.Query.Length > 0 ? "?" + parts.Query : "")}";
    }
}
