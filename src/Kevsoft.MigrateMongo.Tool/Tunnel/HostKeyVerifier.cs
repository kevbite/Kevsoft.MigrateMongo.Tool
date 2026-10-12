using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kevsoft.MigrateMongo.Tool.Tunnel;

/// <summary>Checks an SSH server's host key against a pinned fingerprint or an OpenSSH known_hosts file.</summary>
internal static class HostKeyVerifier
{
    /// <summary>The fingerprint in <c>ssh-keygen -lf</c> format: <c>SHA256:</c> + unpadded base64 of the key blob's SHA-256.</summary>
    public static string Fingerprint(byte[] hostKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    public static bool MatchesFingerprint(byte[] hostKey, string expected) =>
        string.Equals(Fingerprint(hostKey), expected.Trim().TrimEnd('='), StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="knownHosts"/> (lines of an OpenSSH known_hosts file) trusts <paramref name="hostKey"/>
    /// for <paramref name="host"/>:<paramref name="port"/>. Supports hashed hosts, <c>[host]:port</c>, wildcards,
    /// negation and <c>@revoked</c>.
    /// </summary>
    public static bool IsKnown(IEnumerable<string> knownHosts, string host, int port, byte[] hostKey)
    {
        var name = port == 22 ? host : $"[{host}]:{port}";
        var trusted = false;
        foreach (var raw in knownHosts)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            string? marker = null;
            if (fields[0].StartsWith('@'))
            {
                marker = fields[0];
                fields = fields[1..];
            }

            if (fields.Length < 3 || marker == "@cert-authority" || !MatchesHost(fields[0], name) || !SameKey(fields[2], hostKey))
            {
                continue;
            }

            if (marker == "@revoked")
            {
                return false;
            }

            trusted = true;
        }

        return trusted;
    }

    private static bool SameKey(string base64, byte[] hostKey)
    {
        try
        {
            return Convert.FromBase64String(base64).AsSpan().SequenceEqual(hostKey);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool MatchesHost(string patterns, string name)
    {
        if (patterns.StartsWith("|1|", StringComparison.Ordinal))
        {
            return MatchesHashed(patterns, name);
        }

        var matched = false;
        foreach (var pattern in patterns.Split(','))
        {
            var negated = pattern.StartsWith('!');
            if (Glob(negated ? pattern[1..] : pattern, name))
            {
                if (negated)
                {
                    return false;
                }

                matched = true;
            }
        }

        return matched;
    }

    private static bool MatchesHashed(string entry, string name)
    {
        var parts = entry.Split('|');
        if (parts.Length != 4)
        {
            return false;
        }

        try
        {
            var hash = HMACSHA1.HashData(Convert.FromBase64String(parts[2]), Encoding.UTF8.GetBytes(name));
            return hash.AsSpan().SequenceEqual(Convert.FromBase64String(parts[3]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool Glob(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
